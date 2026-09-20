using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The Bosses column read END TO END out of a real session
/// (<c>tests/fixtures/session-2026-09-bosspaths.rec</c>, 2026-09-20).
/// </summary>
/// <remarks>
/// THE CAPTURE THAT WAS MISSING, and it was missing for a reason worth keeping written down: a
/// recording can only contain reads the running build performed. session-2026-09-catalogue.rec
/// proved the COUNT at 0x9C against 206 areas, and it could never prove anything past the
/// pointer beside it, because the build that took it never followed one. This one was taken
/// after the column landed, so the MonsterVarieties rows behind it are in the file.
///
/// WHAT IT SETTLES. All 442 rows decode, 206 of them name a boss, and every one of those 206
/// resolves to the SAME PATHS, IN THE SAME ORDER, as data/area-bosses.json - which was generated
/// months earlier from a third-party export of the same table by an entirely different route.
/// Not one difference in either direction, and not one area the live read found that the file
/// does not have.
///
/// That is three independent things agreeing at once: the column offset (0x9C, arithmetic on
/// dat-schema's order), the array reading (count then pointer, 16-byte entries), and the claim
/// that a foreign reference's ROW pointer sits at the column start with the table at +8. A
/// mistake in any of them does not produce 206 exact strings.
///
/// THE STATE IS Escape RATHER THAN InGame, which is only where the atlas happened to be open
/// when this was taken and changes nothing: the route to the table is an atlas node, and the
/// table is loaded once and never freed.
/// </remarks>
public class WorldAreaBossSessionTests
{
    private static DirectoryInfo Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return dir;
        }
    }

    private static ReplayMemoryReader Load()
        => ReplayMemoryReader.Load(File.OpenRead(
            Path.Combine(Root.FullName, "tests", "fixtures", "session-2026-09-bosspaths.rec")));

    private static WorldAreaCatalogue Read(ReplayMemoryReader replay)
    {
        OffsetSchema schema = RealSessionTests.LiveSchema();
        GameChainAddresses chain = GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]);

        var elements = new UiElementReader(replay, schema);
        List<AtlasNode> nodes = new AtlasReader(replay, schema, elements).Read(chain.UiRoot, new UiScale(3440, 1440, 0));
        Assert.True(nodes.Count > 100, $"only {nodes.Count} nodes read off the panel");

        var catalogue = new WorldAreaCatalogue(replay, schema);
        foreach (AtlasNode node in nodes)
        {
            if (node.MapId.Length > 0 && catalogue.ReadFromNode(node.Address))
            {
                break;
            }
        }

        Assert.NotNull(catalogue.Table);
        Assert.Equal(442, catalogue.All.Count);
        return catalogue;
    }

    /// <summary>Every boss path the client holds, against the table this tool ships.</summary>
    [Fact]
    public void TheBossPathsComeBackWholeAndAgreeWithTheShippedTable()
    {
        using ReplayMemoryReader replay = Load();
        WorldAreaCatalogue catalogue = Read(replay);

        IReadOnlyDictionary<string, IReadOnlyList<string>> live = catalogue.BossesByArea();
        AreaBosses shipped = AreaBosses.Load(Path.Combine(Root.FullName, "data", "area-bosses.json"));

        Assert.Equal(206, live.Count);
        Assert.Equal(206, shipped.Count);

        // ORDER INCLUDED, not just the set. The first path is the one an arena's label and its
        // icon family are taken from, so a column read in the wrong order would put the Baron's
        // wolf on his human form's marker - and the two lists agreeing as SEQUENCES is what
        // says the entries are walked the way the table wrote them.
        List<string> differs = [.. live
            .Where(pair => !shipped.Of(pair.Key).SequenceEqual(pair.Value, StringComparer.Ordinal))
            .Select(pair => $"{pair.Key}: live [{string.Join(", ", pair.Value)}]"
                + $" file [{string.Join(", ", shipped.Of(pair.Key))}]")];

        Assert.Empty(differs);
    }

    /// <summary>
    /// The three shapes the column takes, by name, so an empty read cannot pass the test above.
    /// </summary>
    /// <remarks>
    /// A LIST THAT CAME BACK EMPTY WOULD AGREE WITH ITSELF. The comparison above walks what the
    /// live read found, so a walk that found nothing compares nothing and passes - which is
    /// exactly the failure a regeneration of the shipped file could hide. These three are
    /// asserted outright: the ordinary one boss, the two forms of one boss in the table's own
    /// order, and an area that names nobody.
    /// </remarks>
    [Fact]
    public void TheOrdinaryCaseTheTwoFormCaseAndTheEmptyOneAllRead()
    {
        using ReplayMemoryReader replay = Load();
        WorldAreaCatalogue catalogue = Read(replay);

        Assert.Equal(
            ["Metadata/Monsters/LeagueAbyss/LichBoss/KulemakBoss"],
            Assert.IsType<WorldArea>(catalogue.Of("Abyss_Pinnacle")).Bosses);

        // FIVE ON ONE AREA, which is the longest list in the table and the case a column read as
        // a single value would silently truncate to its first entry.
        Assert.Equal(5, Assert.IsType<WorldArea>(catalogue.Of("BossRush_Area1")).Bosses.Count);

        // And a hideout names nobody - the 236 rows that are not about bosses at all, which is
        // why BossesByArea leaves them out rather than handing AreaBosses an empty list.
        Assert.Empty(Assert.IsType<WorldArea>(catalogue.Of("HideoutCanal")).Bosses);
        Assert.DoesNotContain("HideoutCanal", catalogue.BossesByArea());
    }
}
