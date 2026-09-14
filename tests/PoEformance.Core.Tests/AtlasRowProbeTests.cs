using PoEformance.Core.Schema;
using PoEformance.Game.Diagnostics;

namespace PoEformance.Core.Tests;

/// <summary>
/// The atlas-row sweep, against synthetic positions built from the schema.
/// </summary>
/// <remarks>
/// A fixture built from the schema follows the schema wherever it goes, so nothing here vouches
/// for an offset - that is what a capture is for. What it does cover is the behaviour the sweep
/// exists to have, and each of these is a way the probe could quietly become useless:
///
/// - it reads EVERY node rather than the first, and says how many rows it reached. The whole
///   reason this exists is that the previous probe sampled two nodes and three captures between
///   them held exactly one row.
/// - it tells apart a row shared by several maps from a row per map, because that is the question
///   a sample cannot answer and the one that says what the column IS.
/// - it reports reaching nothing as an ambiguous result rather than a finding. On a replay a zero
///   means the recording lacks the reads, not that the game lacks the data, and that inference was
///   drawn wrongly once in this project already.
/// </remarks>
public class AtlasRowProbeTests
{
    private const ulong Node = 0x30_0000;
    private const ulong Storage = 0x40_0000;
    private const ulong Data = 0x50_0000;
    private const ulong Row = 0x60_0000;
    private const ulong Passive = 0x70_0000;
    private const ulong Objective = 0x71_0000;
    private const ulong Strings = 0x80_0000;

    private static OffsetSchema Schema => WorldAreaCatalogueTests.Schema;

    private static ulong Text(FakeMemoryReader fake, ulong at, string text)
    {
        fake.Place(at, new byte[0x100]);
        fake.PlaceUtf16(at, text);
        return at;
    }

    /// <summary>A node whose position row carries a passive and an objective.</summary>
    /// <param name="nodes">How many nodes share the one row; each gets its own element.</param>
    private static (FakeMemoryReader Fake, List<ProbedNode> Nodes) Fixture(
        int nodes = 1,
        bool sameMap = true,
        bool withObjective = true)
    {
        StructDef node = Schema.Structs["AtlasNode"];
        StructDef data = Schema.Structs["AtlasNodeData"];
        StructDef row = Schema.Structs["EndgameMapAtlasRow"];
        StructDef passive = Schema.Structs["PassiveSkillsRow"];
        StructDef objective = Schema.Structs["EndgameMapObjectivesRow"];

        var fake = new FakeMemoryReader();
        var probed = new List<ProbedNode>();

        for (int i = 0; i < nodes; i++)
        {
            ulong element = Node + ((ulong)i * 0x1000);
            ulong storage = Storage + ((ulong)i * 0x1000);
            ulong d = Data + ((ulong)i * 0x1000);

            fake.Place(element + (ulong)(int)node.Constants["DataStoragePtr"], storage);
            fake.Place(storage + (ulong)(int)node.Constants["DataPtr"], d);
            fake.Place(d + (ulong)data.OffsetOf("AtlasRowPtr"), Row);
            probed.Add(new ProbedNode(i, element, sameMap ? "MapRugosa" : $"MapNumber{i}"));
        }

        // The one position row every node points at.
        fake.Place(Row, new byte[(int)row.Constants["ComputedRowSize"]]);
        fake.Place(Row + (ulong)row.OffsetOf("PassivesRef"), Passive);
        fake.Place(Passive + (ulong)passive.OffsetOf("IdPtr"), Text(fake, Strings, "AtlasOutsideFortressPath72"));
        fake.Place(Passive + (ulong)passive.OffsetOf("NamePtr"), Text(fake, Strings + 0x1000, "[DNT] Atlas Outside Path"));
        fake.Place(Passive + (ulong)passive.OffsetOf("GraphId"), (ushort)861);

        if (withObjective)
        {
            fake.Place(Row + (ulong)row.OffsetOf("MapObjectiveRef"), Objective);
            fake.Place(Objective + (ulong)objective.OffsetOf("IdPtr"), Text(fake, Strings + 0x2000, "KillAllMonsters"));
            fake.Place(Objective + (ulong)objective.OffsetOf("ObjectiveTextPtr"), Text(fake, Strings + 0x3000, "Slay all monsters"));
            fake.Place(Objective + (ulong)objective.OffsetOf("CompletionTextPtr"), Text(fake, Strings + 0x4000, "Area cleared"));
        }

        return (fake, probed);
    }

    private static string Run(FakeMemoryReader fake, List<ProbedNode> nodes)
        => string.Join('\n', new AtlasRowProbe(fake, Schema).Probe(nodes));

    [Fact]
    public void ItSweepsEveryNodeAndSaysHowManyRowsItReached()
    {
        (FakeMemoryReader fake, List<ProbedNode> nodes) = Fixture(nodes: 4);
        string said = Run(fake, nodes);

        Assert.Contains("4 nodes, 4 with a readable atlas row, 1 distinct rows", said, StringComparison.Ordinal);
        Assert.Contains("AtlasOutsideFortressPath72", said, StringComparison.Ordinal);
        Assert.Contains("GraphId 861", said, StringComparison.Ordinal);
    }

    [Fact]
    public void ItTellsARowSharedAcrossMapsFromARowPerMap()
    {
        // The distinction the single sample could not make. Four nodes of ONE map sharing a row
        // says nothing; four nodes of four different maps sharing it says the row is a POSITION.
        Assert.Contains(
            "0 by nodes of DIFFERENT maps",
            Run(Fixture(nodes: 4).Fake, Fixture(nodes: 4).Nodes),
            StringComparison.Ordinal);

        (FakeMemoryReader fake, List<ProbedNode> nodes) = Fixture(nodes: 4, sameMap: false);
        Assert.Contains("1 by nodes of DIFFERENT maps", Run(fake, nodes), StringComparison.Ordinal);
    }

    [Fact]
    public void ItReadsTheObjectiveTextWhichIsTheHalfWorthShowing()
    {
        (FakeMemoryReader fake, List<ProbedNode> nodes) = Fixture();
        string said = Run(fake, nodes);

        Assert.Contains("1 carry a MapObjective", said, StringComparison.Ordinal);
        Assert.Contains("\"Slay all monsters\"", said, StringComparison.Ordinal);
        Assert.Contains("\"Area cleared\"", said, StringComparison.Ordinal);
    }

    [Fact]
    public void AndSaysSoPlainlyWhenAColumnIsEmptyRatherThanSkippingTheLine()
    {
        // The reading the one real sample gives: a path position carries no objective at all. A
        // probe that printed nothing there would look like a probe that had not looked.
        (FakeMemoryReader fake, List<ProbedNode> nodes) = Fixture(withObjective: false);
        string said = Run(fake, nodes);

        Assert.Contains("0 carry a MapObjective", said, StringComparison.Ordinal);
        Assert.Contains("MapObjective 0x0 (not a pointer)", said, StringComparison.Ordinal);
        Assert.Contains("Stats        none", said, StringComparison.Ordinal);
    }

    [Fact]
    public void ReachingNothingIsReportedAsAmbiguousRatherThanAsAFinding()
    {
        // THE TEST THAT MATTERS MOST HERE, because the wrong version of this message is how a
        // replay artefact becomes a reported discovery - which happened in this project before
        // the mistake was caught. A zero has two meanings and the line has to carry both.
        string said = string.Join('\n', new AtlasRowProbe(new FakeMemoryReader(), Schema)
            .Probe([new ProbedNode(0, Node, "MapRugosa")]));

        Assert.Contains("1 nodes, 0 with a readable atlas row", said, StringComparison.Ordinal);
        Assert.Contains("ON A REPLAY", said, StringComparison.Ordinal);
        Assert.Contains("not a fact about the game", said, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyAtlasSaysNothingAtAll()
        => Assert.Empty(new AtlasRowProbe(new FakeMemoryReader(), Schema).Probe([]));
}
