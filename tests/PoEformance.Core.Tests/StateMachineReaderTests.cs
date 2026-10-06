using PoEformance.Core.Schema;
using PoEformance.Game.Components;

namespace PoEformance.Core.Tests;

/// <summary>The StateMachine component: names joined to values, and the listeners on it.</summary>
public class StateMachineReaderTests
{
    private static OffsetSchema Schema() => MonolithFixture.ShippedSchema();

    [Fact]
    public void TheStatesComeOutNamed_AndOneCanBeAskedFor()
    {
        OffsetSchema schema = Schema();
        var fixture = new MonolithFixture(schema, holes: 5, activated: 1, rerolled: true);
        var reader = new StateMachineReader(fixture.Reader, schema);

        IReadOnlyList<MachineState> states = reader.Read(MonolithFixture.Machine);

        Assert.Equal(
            [new MachineState("sockets", 5), new MachineState("activated", 1), new MachineState("is_rerolled", 1)],
            states);
        Assert.Equal(5, reader.ValueOf(MonolithFixture.Machine, "sockets"));
        Assert.Equal(1, reader.ValueOf(MonolithFixture.Machine, "IS_REROLLED"));
        Assert.Null(reader.ValueOf(MonolithFixture.Machine, "light_colour"));
    }

    [Fact]
    public void TheNamesAreReadOnce_AndTheValuesEveryTime()
    {
        OffsetSchema schema = Schema();
        var fixture = new MonolithFixture(schema);
        var reader = new StateMachineReader(fixture.Reader, schema);

        reader.Read(MonolithFixture.Machine);
        long before = fixture.Reader.Reads;
        reader.Read(MonolithFixture.Machine);
        long second = fixture.Reader.Reads - before;

        // Two vector pointers, the definition and its names base, three values: the three
        // string headers are not read again.
        Assert.True(second <= 7, $"the second read cost {second} reads - were the names re-read?");
    }

    [Fact]
    public void TheListenersAreTheNodesInOrder_AndNothingReadsEmpty()
    {
        OffsetSchema schema = Schema();
        var fixture = new MonolithFixture(schema);
        var reader = new StateMachineReader(fixture.Reader, schema);

        Assert.Equal([MonolithFixture.DecoyNode, MonolithFixture.Node], reader.Listeners(MonolithFixture.Machine));
        Assert.Empty(reader.Listeners(0xDEAD_BEEF));
        Assert.Empty(reader.Read(0xDEAD_BEEF));
        Assert.Empty(reader.Read(0));
    }
}
