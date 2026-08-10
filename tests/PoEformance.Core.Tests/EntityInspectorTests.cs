using PoEformance.Core.Schema;
using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// Taking an entity apart, and saying which parts nothing understands.
/// </summary>
/// <remarks>
/// The claim being tested is the one the whole feature rests on: "the schema does not
/// describe this". It has to be right in both directions. Calling a described component
/// unknown sends someone off to reverse-engineer what is already done; calling an unknown one
/// described hides the only thing worth looking at.
///
/// The fixtures build a real entity in fake memory - component vector, hash bucket, name
/// strings - because the layout being read is exactly what could be got wrong.
/// </remarks>
public class EntityInspectorTests
{
    private const ulong EntityAt = 0x2000_0000_0000;
    private const ulong DetailsAt = 0x2000_0001_0000;
    private const ulong LookupAt = 0x2000_0002_0000;
    private const ulong BucketAt = 0x2000_0003_0000;
    private const ulong ComponentsAt = 0x2000_0004_0000;
    private const ulong NamesAt = 0x2000_0005_0000;

    private static OffsetSchema Schema()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "schema", "poe2.offsets.json")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return SchemaJson.Load(Path.Combine(dir.FullName, "schema", "poe2.offsets.json"));
    }

    /// <summary>
    /// Builds one entity carrying the named components, laid out as the game lays them out.
    /// </summary>
    private static FakeMemoryReader Entity(
        OffsetSchema schema, string path, ulong at, ulong details, ulong bucket, ulong components, ulong names,
        params string[] carried)
        => Entity(new FakeMemoryReader(), schema, path, at, details, bucket, components, names, carried);

    /// <summary>Adds another entity to a reader, so a survey has more than one to count.</summary>
    private static FakeMemoryReader Entity(
        FakeMemoryReader reader, OffsetSchema schema, string path,
        ulong at, ulong details, ulong bucket, ulong components, ulong names,
        params string[] carried)
    {
        StructDef entity = schema.Structs["Entity"];
        StructDef detail = schema.Structs["EntityDetails"];
        StructDef lookup = schema.Structs["ComponentLookup"];
        StructDef bucketDef = schema.Structs["StdBucket"];
        StructDef entry = schema.Structs["ComponentLookupEntry"];
        int entrySize = (int)entry.Constants["Size"];

        ulong lookupAt = details + 0x400;
        reader.Place(at, new byte[0x200]);
        reader.Place(details, new byte[0x600]);

        reader.Place(at + (ulong)entity.OffsetOf("Id"), 4242u);
        reader.Place(at + (ulong)entity.OffsetOf("EntityDetailsPtr"), details);
        reader.Place(at + (ulong)entity.OffsetOf("ComponentsVec"), components);
        reader.Place(at + (ulong)entity.OffsetOf("ComponentsVecLast"), components + ((ulong)carried.Length * 8));
        reader.PlaceStdWString(details + (ulong)detail.OffsetOf("Path"), path, at + 0x180);
        reader.Place(details + (ulong)detail.OffsetOf("ComponentLookupPtr"), lookupAt);

        // The bucket is INLINE at lookup+0x28 rather than a pointer to one - its fields sit
        // right there. Placing a pointer instead reads the bucket out of whatever the pointer
        // happens to be made of, and every component quietly disappears.
        ulong bucketAt = lookupAt + (ulong)lookup.OffsetOf("Bucket");
        reader.Place(bucket, new byte[0x200 + (entrySize * carried.Length)]);
        reader.Place(bucketAt + (ulong)bucketDef.OffsetOf("Data"), bucket + 0x100);
        reader.Place(bucketAt + (ulong)bucketDef.OffsetOf("DataLast"), bucket + 0x100 + ((ulong)carried.Length * (ulong)entrySize));
        reader.Place(bucketAt + (ulong)bucketDef.OffsetOf("Capacity"), carried.Length);

        reader.Place(components, new byte[8 * Math.Max(carried.Length, 1)]);
        reader.Place(names, new byte[0x100 * Math.Max(carried.Length, 1)]);

        for (int i = 0; i < carried.Length; i++)
        {
            ulong componentAddress = components + 0x10_0000 + ((ulong)i * 0x1000);
            ulong nameAddress = names + ((ulong)i * 0x100);
            ulong entryAt = bucket + 0x100 + ((ulong)i * (ulong)entrySize);

            reader.Place(components + ((ulong)i * 8), componentAddress);
            reader.Place(componentAddress, new byte[0x100]);
            reader.PlaceUtf8(nameAddress, carried[i]);
            reader.Place(entryAt + (ulong)entry.OffsetOf("NamePtr"), nameAddress);
            reader.Place(entryAt + (ulong)entry.OffsetOf("Index"), i);
        }

        return reader;
    }

    private static EntityView Look(EntityInspector inspector, EntityRequest request)
    {
        inspector.Request(request);
        inspector.Service();
        return inspector.View;
    }

    [Fact]
    public void AnEntityIsTakenApartIntoItsComponents()
    {
        OffsetSchema schema = Schema();
        FakeMemoryReader reader = Entity(
            schema, "Metadata/Monsters/Skeleton", EntityAt, DetailsAt, BucketAt, ComponentsAt, NamesAt,
            "Render", "Life", "Positioned");

        EntityView view = Look(new EntityInspector(reader, schema), new EntityRequest(true, EntityAt));

        Assert.Equal("Metadata/Monsters/Skeleton", view.Path);
        Assert.Equal(4242u, view.Id);
        Assert.Equal(3, view.Components.Count);
        Assert.Contains(view.Components, component => component.Name == "Life" && component.Address != 0);
    }

    [Fact]
    public void ComponentsTheSchemaDoesNotDescribeAreMarkedAsSUCH()
    {
        // The claim the whole feature rests on, and it has to be right both ways round.
        OffsetSchema schema = Schema();
        FakeMemoryReader reader = Entity(
            schema, "Metadata/Monsters/Skeleton", EntityAt, DetailsAt, BucketAt, ComponentsAt, NamesAt,
            "Render", "TriggerableBlockage");

        EntityView view = Look(new EntityInspector(reader, schema), new EntityRequest(true, EntityAt));

        Assert.True(view.Components.Single(c => c.Name == "Render").Described);
        Assert.False(view.Components.Single(c => c.Name == "TriggerableBlockage").Described);
        Assert.Equal(1, view.Undescribed);
    }

    [Fact]
    public void TheUndescribedOnesComeFirstBecauseTheyAreThePoint()
    {
        OffsetSchema schema = Schema();
        FakeMemoryReader reader = Entity(
            schema, "Metadata/Monsters/Skeleton", EntityAt, DetailsAt, BucketAt, ComponentsAt, NamesAt,
            "Render", "SomethingNobodyHasRead", "Life");

        EntityView view = Look(new EntityInspector(reader, schema), new EntityRequest(true, EntityAt));

        Assert.Equal("SomethingNobodyHasRead", view.Components[0].Name);
    }

    /// <summary>What is on an entity with a clock running, and what has no clock at all.</summary>
    /// <remarks>
    /// The question this exists to answer: a flame wall obviously has a duration - the game
    /// prints it in the tooltip - and it carries no DiesAfterTime component whatsoever, so the
    /// classification cannot see it expire. If the duration is anywhere on the entity it is
    /// here, on the Buffs component, which is where the reference reads every other duration
    /// from. Reading it for the SELECTED entity only is what makes the vector walk affordable.
    ///
    /// The permanent case is the one worth building: a buff that never ends does not report a
    /// duration of zero, it reports INFINITY, and formatted naively that reads as a number
    /// somebody could act on. The reference's buff bar tells the two apart by exactly this
    /// test, and so does the browser.
    /// </remarks>
    [Fact]
    public void TimedEffectsOnAnEntityAreReadWithTheirClocks()
    {
        OffsetSchema schema = Schema();
        FakeMemoryReader reader = Entity(
            schema, "Metadata/Monsters/Firewall", EntityAt, DetailsAt, BucketAt, ComponentsAt, NamesAt,
            "Render", "Buffs");

        StructDef buffs = schema.Structs["Buffs"];
        StructDef effect = schema.Structs["StatusEffect"];
        StructDef definition = schema.Structs["BuffDefinition"];
        int stride = (int)buffs.Constants["StatusEffectStructSize"];

        // The Buffs component the entity builder placed, and a two-entry vector after it.
        EntityView placed = Look(new EntityInspector(reader, schema), new EntityRequest(true, EntityAt));
        ulong component = placed.Components.Single(c => c.Name == "Buffs").Address;

        const ulong VectorAt = 0x3000_0000_0000;
        const ulong TimedDefAt = 0x3000_0001_0000;
        const ulong ForeverDefAt = 0x3000_0002_0000;
        const ulong TimedNameAt = 0x3000_0003_0000;
        const ulong ForeverNameAt = 0x3000_0004_0000;

        reader.Place(component + (ulong)buffs.OffsetOf("StatusEffectFirst"), VectorAt);
        reader.Place(component + (ulong)buffs.OffsetOf("StatusEffectLast"), VectorAt + (ulong)(2 * stride));

        reader.Place(VectorAt + (ulong)effect.OffsetOf("BuffDefinitionPtr"), TimedDefAt);
        reader.Place(VectorAt + (ulong)effect.OffsetOf("TimeLeft"), 4.5f);
        reader.Place(VectorAt + (ulong)effect.OffsetOf("TotalTime"), 10.3f);
        reader.Place(TimedDefAt + (ulong)definition.OffsetOf("Name"), TimedNameAt);
        reader.PlaceUtf16(TimedNameAt, "wall_of_fire");

        ulong second = VectorAt + (ulong)stride;
        reader.Place(second + (ulong)effect.OffsetOf("BuffDefinitionPtr"), ForeverDefAt);
        reader.Place(second + (ulong)effect.OffsetOf("TimeLeft"), float.PositiveInfinity);
        reader.Place(second + (ulong)effect.OffsetOf("TotalTime"), float.PositiveInfinity);
        reader.Place(ForeverDefAt + (ulong)definition.OffsetOf("Name"), ForeverNameAt);
        reader.PlaceUtf16(ForeverNameAt, "something_permanent");

        EntityView view = Look(new EntityInspector(reader, schema), new EntityRequest(true, EntityAt));

        Assert.Equal(2, view.Timed.Count);

        TimedEffect timed = view.Timed.Single(e => e.Name == "wall_of_fire");
        Assert.Equal(4.5f, timed.TimeLeft, 3);
        Assert.Equal(10.3f, timed.TotalTime, 3);

        // Infinity, not zero. A duration of zero would read as "about to expire", which is
        // the opposite of what a permanent effect means.
        TimedEffect forever = view.Timed.Single(e => e.Name == "something_permanent");
        Assert.False(float.IsFinite(forever.TimeLeft));
    }

    /// <summary>An entity with no Buffs component reports no effects rather than null.</summary>
    [Fact]
    public void AnEntityWithoutBuffsHasNoEffectsAndDoesNotThrow()
    {
        OffsetSchema schema = Schema();
        FakeMemoryReader reader = Entity(
            schema, "Metadata/Monsters/Skeleton", EntityAt, DetailsAt, BucketAt, ComponentsAt, NamesAt,
            "Render", "Life");

        EntityView view = Look(new EntityInspector(reader, schema), new EntityRequest(true, EntityAt));

        Assert.Empty(view.Timed);
        Assert.Equal(string.Empty, view.EffectsNote);
    }

    /// <summary>Carrying Buffs with nothing on it does not look like carrying no Buffs.</summary>
    /// <remarks>
    /// The first version of this drew nothing at all when there was nothing to draw, and a
    /// screenshot of that answered no question: "no Buffs component", "nothing on it", "could
    /// not be read" and "you are running a build without the feature" were one blank space.
    /// The whole reason the read was added was to settle a question by looking.
    /// </remarks>
    [Fact]
    public void CarryingBuffsWithNothingOnItSaysSo()
    {
        OffsetSchema schema = Schema();
        FakeMemoryReader reader = Entity(
            schema, "Metadata/Monsters/Anomalies/Firewall", EntityAt, DetailsAt, BucketAt, ComponentsAt, NamesAt,
            "Render", "Buffs");

        StructDef buffs = schema.Structs["Buffs"];
        EntityView placed = Look(new EntityInspector(reader, schema), new EntityRequest(true, EntityAt));
        ulong component = placed.Components.Single(c => c.Name == "Buffs").Address;

        // An empty vector: both ends readable, and equal.
        const ulong VectorAt = 0x3100_0000_0000;
        reader.Place(component + (ulong)buffs.OffsetOf("StatusEffectFirst"), VectorAt);
        reader.Place(component + (ulong)buffs.OffsetOf("StatusEffectLast"), VectorAt);

        EntityView view = Look(new EntityInspector(reader, schema), new EntityRequest(true, EntityAt));

        Assert.Empty(view.Timed);
        Assert.Contains("nothing on it", view.EffectsNote, StringComparison.Ordinal);
    }

    /// <summary>The entity's own stat pairs are read, and a truncated list says it is one.</summary>
    /// <remarks>
    /// The Stats component is where the game keeps what it believes about an entity, as a
    /// flat vector of (id, value). It is the remaining place a flame wall's ten-second
    /// duration could be sitting, now that its Buffs turned out to be empty - and it is worth
    /// reading for its own sake, because those ids are the game's own and can be looked up.
    ///
    /// The cap is reported rather than applied silently: a list that stops at 256 while
    /// claiming to be everything is how somebody concludes a stat is absent when it is merely
    /// off the end.
    /// </remarks>
    [Fact]
    public void TheEntitysOwnStatPairsAreRead()
    {
        OffsetSchema schema = Schema();
        FakeMemoryReader reader = Entity(
            schema, "Metadata/Monsters/Anomalies/Firewall", EntityAt, DetailsAt, BucketAt, ComponentsAt, NamesAt,
            "Render", "Stats");

        StructDef layout = schema.Structs["Stats"];
        EntityView placed = Look(new EntityInspector(reader, schema), new EntityRequest(true, EntityAt));
        ulong component = placed.Components.Single(c => c.Name == "Stats").Address;

        const ulong PairsAt = 0x3200_0000_0000;
        int at = layout.OffsetOf("StatsInternalStatsVector");
        reader.Place(component + (ulong)at, PairsAt);
        reader.Place(component + (ulong)(at + 8), PairsAt + 16);      // two pairs

        var block = new byte[16];
        BitConverter.TryWriteBytes(block.AsSpan(0, 4), 351u);          // skill_effect_duration
        BitConverter.TryWriteBytes(block.AsSpan(4, 4), 10300);         // milliseconds
        BitConverter.TryWriteBytes(block.AsSpan(8, 4), 347u);          // base_skill_effect_duration
        BitConverter.TryWriteBytes(block.AsSpan(12, 4), 10000);
        reader.Place(PairsAt, block);

        EntityView view = Look(new EntityInspector(reader, schema), new EntityRequest(true, EntityAt));

        Assert.Equal(2, view.Numbers.Count);
        Assert.Equal(10300, view.Numbers.Single(s => s.Id == 351).Value);
        Assert.Contains("2 stats", view.StatsNote, StringComparison.Ordinal);
    }

    /// <summary>A Stats component nobody could read says THAT, rather than "no numbers".</summary>
    [Fact]
    public void AStatsComponentThatCannotBeReadIsNotReportedAsEmpty()
    {
        OffsetSchema schema = Schema();
        FakeMemoryReader reader = Entity(
            schema, "Metadata/Monsters/Anomalies/Firewall", EntityAt, DetailsAt, BucketAt, ComponentsAt, NamesAt,
            "Render", "Stats");

        EntityView view = Look(new EntityInspector(reader, schema), new EntityRequest(true, EntityAt));

        Assert.Empty(view.Numbers);
        Assert.Contains("could not be read", view.StatsNote, StringComparison.Ordinal);
    }

    /// <summary>A Buffs component nobody could read says THAT, rather than "nothing on it".</summary>
    [Fact]
    public void ABuffsComponentThatCannotBeReadIsNotReportedAsEmpty()
    {
        OffsetSchema schema = Schema();
        FakeMemoryReader reader = Entity(
            schema, "Metadata/Monsters/Anomalies/Firewall", EntityAt, DetailsAt, BucketAt, ComponentsAt, NamesAt,
            "Render", "Buffs");

        // Nothing placed at the component's vector at all, which is what a replay of a
        // session that never read it looks like.
        EntityView view = Look(new EntityInspector(reader, schema), new EntityRequest(true, EntityAt));

        Assert.Empty(view.Timed);
        Assert.Contains("could not be read", view.EffectsNote, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAddressThatIsNotAnEntityIsReportedRatherThanThrown()
    {
        OffsetSchema schema = Schema();

        EntityView view = Look(
            new EntityInspector(new FakeMemoryReader(), schema), new EntityRequest(true, EntityAt));

        Assert.Empty(view.Components);
        Assert.Contains("did not read as an entity", view.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingIsReadUntilTheWindowIsOpen()
    {
        OffsetSchema schema = Schema();
        FakeMemoryReader reader = Entity(
            schema, "Metadata/Monsters/Skeleton", EntityAt, DetailsAt, BucketAt, ComponentsAt, NamesAt, "Render");

        EntityView view = Look(new EntityInspector(reader, schema), new EntityRequest(false, EntityAt));

        Assert.Empty(view.Components);
    }

    [Fact]
    public void ASurveyCountsHowManyEntitiesCarryEachComponent()
    {
        // The question that cannot be asked one entity at a time.
        OffsetSchema schema = Schema();
        FakeMemoryReader reader = Entity(
            schema, "Metadata/Monsters/Skeleton", EntityAt, DetailsAt, BucketAt, ComponentsAt, NamesAt,
            "Render", "RareUnreadThing");

        EntityView view = Look(
            new EntityInspector(reader, schema),
            new EntityRequest(true, 0, Survey: [EntityAt], SurveySequence: 1));

        Assert.Equal(1, view.SurveyedEntities);
        Assert.Equal(1, view.Survey.Single(entry => entry.Name == "Render").Count);
        Assert.False(view.Survey.Single(entry => entry.Name == "RareUnreadThing").Described);
    }

    [Fact]
    public void TheSurveyPutsTheRAREUndescribedOnesFirst()
    {
        // A component two entities in an area carry is a far better lead than one everything
        // has - and that difference is invisible until they are counted together. So among the
        // undescribed ones the RARE one has to come first; sorting them by count the usual way
        // round buries the interesting one under the ubiquitous one.
        OffsetSchema schema = Schema();
        const ulong SecondAt = EntityAt + 0x10_0000;

        FakeMemoryReader reader = Entity(
            schema, "Metadata/Monsters/A", EntityAt, DetailsAt, BucketAt, ComponentsAt, NamesAt,
            "Render", "SeenEverywhere", "SeenOnce");

        Entity(
            reader, schema, "Metadata/Monsters/B",
            SecondAt, DetailsAt + 0x10_0000, BucketAt + 0x10_0000, ComponentsAt + 0x10_0000, NamesAt + 0x10_0000,
            "Render", "SeenEverywhere");

        var inspector = new EntityInspector(reader, schema);
        EntityView view = Look(
            inspector, new EntityRequest(true, 0, Survey: [EntityAt, SecondAt], SurveySequence: 1));

        Assert.Equal(2, view.SurveyedEntities);
        Assert.Equal(2, view.Survey.Single(entry => entry.Name == "SeenEverywhere").Count);
        Assert.Equal(1, view.Survey.Single(entry => entry.Name == "SeenOnce").Count);

        // Rarest undescribed first, then the common undescribed one, and only then the ones
        // the schema already describes.
        Assert.Equal("SeenOnce", view.Survey[0].Name);
        Assert.Equal("SeenEverywhere", view.Survey[1].Name);
        Assert.True(view.Survey[2].Described);
    }

    [Fact]
    public void TheSurveyIsTakenONCERatherThanEveryTick()
    {
        // It reads every entity in the area. Doing that thirty times a second would put the
        // reader thread - which also drives auto-flask - permanently behind.
        OffsetSchema schema = Schema();
        FakeMemoryReader reader = Entity(
            schema, "Metadata/Monsters/Skeleton", EntityAt, DetailsAt, BucketAt, ComponentsAt, NamesAt, "Render");

        var inspector = new EntityInspector(reader, schema);
        Look(inspector, new EntityRequest(true, 0, Survey: [EntityAt], SurveySequence: 1));

        // Asking again with the SAME sequence must not re-read; an empty list would wipe the
        // tally if it did.
        EntityView again = Look(inspector, new EntityRequest(true, 0, Survey: [], SurveySequence: 1));

        Assert.Equal(1, again.SurveyedEntities);
        Assert.NotEmpty(again.Survey);
    }

    [Fact]
    public void ASurveySurvivesPickingAnEntityAfterwards()
    {
        // They are shown in the same window, and re-counting on every selection would make
        // the expensive half of the tool fire on an ordinary click.
        OffsetSchema schema = Schema();
        FakeMemoryReader reader = Entity(
            schema, "Metadata/Monsters/Skeleton", EntityAt, DetailsAt, BucketAt, ComponentsAt, NamesAt, "Render");

        var inspector = new EntityInspector(reader, schema);
        Look(inspector, new EntityRequest(true, 0, Survey: [EntityAt], SurveySequence: 1));
        EntityView picked = Look(inspector, new EntityRequest(true, EntityAt, Survey: [EntityAt], SurveySequence: 1));

        Assert.NotEmpty(picked.Components);
        Assert.NotEmpty(picked.Survey);
    }
}
