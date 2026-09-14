using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Diagnostics;
using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>
/// The atlas against a REAL 0.5.5 session with the panel open
/// (<c>tests/fixtures/session-2026-09-atlas.rec</c>, 2026-09-14).
/// </summary>
/// <remarks>
/// THE FIRST ATLAS CAPTURE ON THIS CLIENT, and every atlas offset in the schema was ported
/// rather than measured until it existed. What it settles, one reading each:
///
/// - AtlasNodeData.BiomeId and StatusBits read like a biome and a bit field rather than like
///   padding. The 0.5.5 drift symptom was famously quiet - biome 255, every node Completed, no
///   ids - so "not 255" and "a mix of states" is the test, not a spot value.
/// - MapDataPtr walks three pointers to a real map id.
/// - the BADGE NAME is at +0x2E8 and GameHelper2's +0x278 is NULL on the same child, which is
///   what the layout probe was written to ask.
/// - what hangs off node+0x300 is a PassiveSkills.dat row - the node's atlas passive - and not
///   the WorldAreas row the offset's source documents. WorldAreas is untouched by that, which
///   is checked here too because it is the claim that would otherwise be taken on trust.
///
/// It replays through <see cref="RealSessionTests.LiveSchema"/>, the SHIPPED schema, like the
/// other 0.5.5 fixtures - the frozen pre-patch one describes a different client.
/// </remarks>
public class AtlasSessionTests
{
    private static string FixturePath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "tests", "fixtures", "session-2026-09-atlas.rec");
        }
    }

    private static ReplayMemoryReader Load() => ReplayMemoryReader.Load(File.OpenRead(FixturePath));

    /// <summary>The screen it was played on. Only the scaling of drawn positions depends on it.</summary>
    private static UiScale Viewport => new(3440, 1440, 0);

    private static (AtlasReader Atlas, ulong UiRoot) Attach(ReplayMemoryReader replay)
    {
        OffsetSchema schema = RealSessionTests.LiveSchema();
        GameChainAddresses chain = GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]);

        Assert.Equal(GameStateKind.InGame, chain.State);
        Assert.NotEqual(0UL, chain.UiRoot);

        return (new AtlasReader(replay, schema, new UiElementReader(replay, schema)), chain.UiRoot);
    }

    private static List<AtlasNode> Nodes(ReplayMemoryReader replay)
    {
        (AtlasReader atlas, ulong uiRoot) = Attach(replay);
        return atlas.Read(uiRoot, Viewport);
    }

    [Fact]
    public void TheAtlasReadsAsMapsWithBiomesAndIds()
    {
        using ReplayMemoryReader replay = Load();
        List<AtlasNode> nodes = Nodes(replay);

        Assert.True(nodes.Count > 100, $"only {nodes.Count} nodes read off the panel");

        // 255 is what the drifted offset gave, on every node. The real range is the one
        // AtlasBiomes names, and a capture of a whole atlas covers several of them.
        Assert.All(nodes, node => Assert.InRange(node.Biome, (byte)0, (byte)20));
        Assert.True(nodes.ConvertAll(node => node.Biome).Distinct().Count() >= 3, "one biome for the whole atlas");

        // The id chain is three pointers deep and returned empty when it drifted.
        Assert.Contains(nodes, node => node.MapId.Length > 0);
        Assert.Contains(nodes, node => node.MapId == "MapRugosa");
    }

    [Fact]
    public void ANDTheStatesAreAMixRatherThanAllCompleted()
    {
        // The 0.5.5 symptom exactly: the status byte landed in padding, 0xFF has the completed
        // bit set, and the whole atlas reported finished. A mix is what says the byte is real.
        using ReplayMemoryReader replay = Load();
        List<AtlasNode> nodes = Nodes(replay);

        foreach (AtlasNodeState state in new[] { AtlasNodeState.Completed, AtlasNodeState.Open, AtlasNodeState.Locked })
        {
            Assert.Contains(nodes, node => node.State == state);
        }

        Assert.True(
            nodes.FindAll(node => node.State == AtlasNodeState.Completed).Count < nodes.Count,
            "every node read as completed, which is what a wrong StatusBits looks like");
    }

    [Fact]
    public void TheBadgeNameIsAtTheNewSlotAndGameHelpersIsNull()
    {
        using ReplayMemoryReader replay = Load();
        OffsetSchema schema = RealSessionTests.LiveSchema();
        (AtlasReader atlas, ulong uiRoot) = Attach(replay);

        var probe = new AtlasNodeProbe(replay, schema, new UiElementReader(replay, schema));
        string said = string.Join('\n', probe.Probe(atlas.Read(uiRoot, Viewport)
            .ConvertAll(node => new ProbedNode(node.Index, node.Address, node.MapId))));

        // Content id 0x64 is "Powerful Map Boss" in both references' tables, and that is what the
        // slot POE2Radar reads holds. The slot this project recorded from GameHelper2 is null on
        // the same object - which is why BadgeContentName now names 0x2E8.
        Assert.Contains("id 0x00020064", said, StringComparison.Ordinal);
        Assert.Contains("+0x2E8 -> 0x3DE98B60770 \"Powerful Map Boss\"", said, StringComparison.Ordinal);
        Assert.Contains("+0x278 -> 0x0 (not a pointer)", said, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatHangsOffTheNodeIsAPassiveSkillsRowAndNotAWorldArea()
    {
        using ReplayMemoryReader replay = Load();
        OffsetSchema schema = RealSessionTests.LiveSchema();
        (AtlasReader atlas, ulong uiRoot) = Attach(replay);

        var probe = new AtlasNodeProbe(replay, schema, new UiElementReader(replay, schema));
        string said = string.Join('\n', probe.Probe(atlas.Read(uiRoot, Viewport)
            .ConvertAll(node => new ProbedNode(node.Index, node.Address, node.MapId))));

        // Id, graph id and name off the same row, at the three offsets the community dat-schema
        // computes for PassiveSkills - which is what says this is that table rather than the
        // WorldAreas row the offset was published as.
        Assert.Contains("PassiveSkills row 8197 of 9731", said, StringComparison.Ordinal);
        Assert.Contains("\"AtlasOutsideFortressPath72\"", said, StringComparison.Ordinal);
        Assert.Contains("GraphId 861", said, StringComparison.Ordinal);
        Assert.Contains("\"[DNT] Atlas Outside Path\"", said, StringComparison.Ordinal);
    }

    [Fact]
    public void ANDWorldAreasStillKeepsItsNameWhereThisToolReadsIt()
    {
        // The other half of that: the published note said a name column moved to +0x32 and that
        // +0x08 is an art path now. It is - in PassiveSkills. WorldAreaDat is a different table
        // and reads its name where it always did, on this very recording.
        using ReplayMemoryReader replay = Load();
        OffsetSchema schema = RealSessionTests.LiveSchema();
        GameChainAddresses chain = GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]);

        var areas = new PoEformance.Game.World.WorldAreaReader(replay, schema);
        PoEformance.Game.World.AreaInfo area = areas.Read(chain.WorldData);

        Assert.NotEqual(string.Empty, area.Id);
        Assert.NotEqual(string.Empty, area.Name);
        Assert.DoesNotContain(".dds", area.Name, StringComparison.OrdinalIgnoreCase);
    }
}
