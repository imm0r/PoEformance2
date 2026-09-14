using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Diagnostics;
using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>
/// The WorldAreas row behind an atlas node, against a REAL 0.5.5 session
/// (<c>tests/fixtures/session-2026-09-worldareas.rec</c>, 2026-09-14).
/// </summary>
/// <remarks>
/// THE CAPTURE THAT ANSWERED WHETHER data/atlas-maps.json COULD GO AWAY, and it answered
/// mostly yes and partly no, which is why the readings are pinned here rather than summarised:
///
/// - the row size DIVIDES OUT OF THE TABLE at 0x2E0 over 442 rows, which is exactly what
///   dat-schema computes for WorldAreas' 80 columns. Everything else here rests on that.
/// - IsUniqueMapArea works: the one unique map among the sampled six reads 1 and the rest 0.
/// - the Tags column is a COUNT and a POINTER, and the game's own tags carry "map_tower" on a
///   tower and biome tags besides - but only "map" on a citadel, a unique, a quest map and a
///   claimable hideout, so the curated file's arbiter, quest and hideout are not in there.
/// - IsHideout stays unsettled, deliberately: every sampled row is a map area and reads 0,
///   which a correct column and a misplaced one do alike. The test says that rather than
///   pretending the zero means something. A later capture walked the whole table and settled it
///   - the column is right - see WorldAreaCatalogueSessionTests. This one is kept as it was,
///   because six sampled maps genuinely could not tell the two apart.
/// </remarks>
public class WorldAreaSessionTests
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
            return Path.Combine(dir.FullName, "tests", "fixtures", "session-2026-09-worldareas.rec");
        }
    }

    private static ReplayMemoryReader Load() => ReplayMemoryReader.Load(File.OpenRead(FixturePath));

    private static string Probe(ReplayMemoryReader replay)
    {
        OffsetSchema schema = RealSessionTests.LiveSchema();
        GameChainAddresses chain = GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]);
        Assert.Equal(GameStateKind.InGame, chain.State);

        var elements = new UiElementReader(replay, schema);
        List<AtlasNode> nodes = new AtlasReader(replay, schema, elements).Read(chain.UiRoot, new UiScale(3440, 1440, 0));
        Assert.True(nodes.Count > 100, $"only {nodes.Count} nodes read off the panel");

        return string.Join('\n', new WorldAreaRowProbe(replay, schema)
            .Probe(nodes.ConvertAll(node => new ProbedNode(node.Index, node.Address, node.MapId))));
    }

    [Fact]
    public void TheTableStatesARowSizeAndTheArithmeticAgreesWithIt()
    {
        // The number the other four offsets depend on. 442 rows is also the whole table, and
        // data/atlas-maps.json has 440 entries - so the catalogue is a table walk, not a file.
        using ReplayMemoryReader replay = Load();

        Assert.Contains(
            "table \"Data/Balance/WorldAreas.dat\": 442 rows of 0x2E0, computed 0x2E0 - AGREE",
            Probe(replay),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheUniqueFlagSeparatesAUniqueMapFromTheRest()
    {
        using ReplayMemoryReader replay = Load();
        string said = Probe(replay);

        Assert.Contains("\"MapUniqueMegalith\"", said, StringComparison.Ordinal);
        Assert.Contains("\"The Ezomyte Megaliths\"", said, StringComparison.Ordinal);
        Assert.Contains("IsUniqueMapArea 1", said, StringComparison.Ordinal);
        Assert.Contains("IsUniqueMapArea 0", said, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTagsColumnIsACountAndAPointerAndCarriesTheTowerTag()
    {
        using ReplayMemoryReader replay = Load();
        string said = Probe(replay);

        Assert.Contains("as (count, pointer): 3 entries", said, StringComparison.Ordinal);
        Assert.Contains("\"map_tower\"", said, StringComparison.Ordinal);
        Assert.Contains("\"swamp_biome\"", said, StringComparison.Ordinal);
        Assert.DoesNotContain("as (begin, end)", said, StringComparison.Ordinal);
    }

    [Fact]
    public void ANDTheCuratedTagsThatTheGameDoesNotCarryReadAsPlainMaps()
    {
        // The half that keeps a file alive: a citadel is "arbiter" in data/atlas-maps.json and
        // carries one tag in the game, and that tag is "map". Pinned because it is the finding,
        // not an accident of this capture - if a later client tags citadels, this test fails and
        // the answer changed.
        using ReplayMemoryReader replay = Load();
        string said = Probe(replay);

        int citadel = said.IndexOf("map MapUberBoss_IronCitadel", StringComparison.Ordinal);
        Assert.True(citadel >= 0, "the Iron Citadel was not among the sampled maps");

        string block = said[citadel..];
        int next = block.IndexOf("\nmap ", StringComparison.Ordinal);
        block = next > 0 ? block[..next] : block;

        Assert.Contains("as (count, pointer): 1 entries", block, StringComparison.Ordinal);
        Assert.Contains("Id \"map\"", block, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBadgeStringSaysMoreThanItsIdDoes()
    {
        // The second badge sample, and the one that turns "0x2E8 holds the name" into something
        // worth a feature: the id is the generic 0x64 that the content file calls "Powerful Map
        // Boss", and the string the game keeps beside it names the tier.
        using ReplayMemoryReader replay = Load();
        OffsetSchema schema = RealSessionTests.LiveSchema();
        GameChainAddresses chain = GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]);
        var elements = new UiElementReader(replay, schema);
        List<AtlasNode> nodes = new AtlasReader(replay, schema, elements).Read(chain.UiRoot, new UiScale(3440, 1440, 0));

        string said = string.Join('\n', new AtlasNodeProbe(replay, schema, elements)
            .Probe(nodes.ConvertAll(node => new ProbedNode(node.Index, node.Address, node.MapId))));

        Assert.Contains("id 0x00020064", said, StringComparison.Ordinal);
        Assert.Contains("\"[DeadlyMapBoss|Deadly Map Boss]\"", said, StringComparison.Ordinal);
        Assert.Contains("+0x278 -> 0x0 (not a pointer)", said, StringComparison.Ordinal);
    }
}
