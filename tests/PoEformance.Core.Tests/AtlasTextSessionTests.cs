using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Diagnostics;
using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>
/// The SENTENCES behind the atlas row, from the capture that finally carried them
/// (<c>tests/fixtures/session-2026-09-atlastext.rec</c>, 2026-09-14).
/// </summary>
/// <remarks>
/// THE OFFSETS ARE NO LONGER ARITHMETIC. Every table the atlas row points at names itself here
/// and states a size the columns were computed against - PassiveSkills 9731 x 0x1E7,
/// EndgameMapObjectives 19 x 0x30, ClientStrings2 861 x 0x34, AtlasPassiveSkillSubtrees 6 x 0x74,
/// Stats 27281 x 0x6A. Five for five. Before this, reading a sentence out of any of them would
/// have been believing an offset on no evidence, and a wrong one comes back as plausible text
/// rather than as an error.
///
/// WHAT THE COLUMNS HOLD is player-facing and localised by the client, which is the whole reason
/// to read it from memory rather than ship it: "Complete all [ContainsAbyss|Abysses]" with its
/// completion "All [ContainsAbyss|Abysses] Complete", "Energise the [ContainsIncursion|Vaal
/// Beacons]", and blocked messages that read like instructions - "Defeat Atziri in the Temple to
/// access this District". The [Code|Display] form is the game's own markup for a linked term, so
/// anything that shows these strips it.
///
/// AND STATS IS NOT EMPTY. The earlier capture reported no row carrying one, over 547 rows, and
/// that was a BUG rather than a finding: the array's count was read through a pointer validator,
/// which returns 0 for any number too small to be a heap address. Read raw it is 95 of 116 rows
/// here and 454 of 547 there, and the ids are what a position GRANTS -
/// map_atlas_node_has_abyss, map_abyss_chance_to_be_ulaman_+%. The lesson is narrow and worth
/// keeping: a count is small by nature, so reading one through a pointer check can only say no.
/// </remarks>
public class AtlasTextSessionTests
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
            return Path.Combine(dir.FullName, "tests", "fixtures", "session-2026-09-atlastext.rec");
        }
    }

    private static ReplayMemoryReader Load() => ReplayMemoryReader.Load(File.OpenRead(FixturePath));

    private static string Probe(ReplayMemoryReader replay)
    {
        OffsetSchema schema = RealSessionTests.LiveSchema();
        GameChainAddresses chain = GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]);
        Assert.Equal(GameStateKind.InGame, chain.State);

        List<AtlasNode> nodes = new AtlasReader(replay, schema, new UiElementReader(replay, schema))
            .Read(chain.UiRoot, new UiScale(3440, 1440, 0));
        Assert.True(nodes.Count > 100, $"only {nodes.Count} nodes read off the panel");

        return string.Join('\n', new AtlasRowProbe(replay, schema).Probe(nodes.ConvertAll(n => new ProbedNode(
            n.Index, n.Address, n.MapId, n.BadgeIds, n.State == AtlasNodeState.Completed))));
    }

    [Fact]
    public void EveryTableTheRowPointsAtNamesItselfAndAgreesWithTheArithmetic()
    {
        // The reading that turns four computed row sizes into measured ones, all in one capture.
        // Until this, the sentences below rested on arithmetic and nothing else.
        using ReplayMemoryReader replay = Load();
        string said = Probe(replay);

        foreach (string agreement in new[]
        {
            "\"Data/Balance/PassiveSkills.dat\": 9731 rows of 0x1E7, computed 0x1E7 - AGREE",
            "\"Data/Balance/EndgameMapObjectives.dat\": 19 rows of 0x30, computed 0x30 - AGREE",
            "\"Data/Balance/ClientStrings2.dat\": 861 rows of 0x34, computed 0x34 - AGREE",
            "\"Data/Balance/AtlasPassiveSkillSubtrees.dat\": 6 rows of 0x74, computed 0x74 - AGREE",
            "\"Data/Balance/Stats.dat\": 27281 rows of 0x6A, computed 0x6A - AGREE",
        })
        {
            Assert.Contains(agreement, said, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("DISAGREE", said, StringComparison.Ordinal);
    }

    [Fact]
    public void TheObjectiveTextsAreSentencesAPersonCouldRead()
    {
        // The half worth showing somebody, and the reason for reading it from the game instead of
        // shipping it: on a German client these come back in German.
        using ReplayMemoryReader replay = Load();
        string said = Probe(replay);

        Assert.Contains("Abyss / objective: \"Complete all [ContainsAbyss|Abysses]\"", said, StringComparison.Ordinal);
        Assert.Contains("Abyss / completion: \"All [ContainsAbyss|Abysses] Complete\"", said, StringComparison.Ordinal);
        Assert.Contains("Incursion / objective: \"Energise the [ContainsIncursion|Vaal Beacons]\"", said, StringComparison.Ordinal);
        Assert.Contains("AbyssDepths / objective: \"Complete the Abyssal Depths\"", said, StringComparison.Ordinal);
    }

    [Fact]
    public void AndTheBlockedMessagesReadLikeInstructions()
    {
        // Three different reasons a position is shut, each naming what to do about it. Nothing a
        // tool could invent, and nothing data/atlas-maps.json has ever carried.
        using ReplayMemoryReader replay = Load();
        string said = Probe(replay);

        Assert.Contains("\"Defeat Atziri in the Temple to access this District\"", said, StringComparison.Ordinal);
        Assert.Contains("\"Close the Northern Abyssal Wounds in order to access this Wound\"", said, StringComparison.Ordinal);
        Assert.Contains(
            "\"Speak to the Lurking Creature in the Well of Souls in order to access this Wound\"",
            said,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StatsIsNotEmptyAndTheEarlierZeroWasABug()
    {
        // 95 of 116 rows. The earlier capture said none of 547, because the array's COUNT was read
        // through a pointer validator that returns 0 for small numbers. This is the regression
        // test for that: a figure that can only be non-zero if the count is read raw.
        using ReplayMemoryReader replay = Load();
        string said = Probe(replay);

        Assert.Contains("69 carry a MapObjective, 36 a BlockedMessage, 95 at least one Stat", said, StringComparison.Ordinal);
        Assert.Contains("map_atlas_node_has_abyss", said, StringComparison.Ordinal);
        Assert.Contains("map_abyss_chance_to_be_ulaman_+%", said, StringComparison.Ordinal);
    }

    [Fact]
    public void TheObjectiveAlsoNamesTheIconTheGameDrawsForIt()
    {
        // Found by the block read rather than looked for: EndgameMapObjectives' fifth column is a
        // MinimapIcons reference, so the game answers "what icon belongs on this" itself. Worth
        // pinning because nothing decodes it yet and the next person should not have to find it.
        using ReplayMemoryReader replay = Load();

        Assert.Contains("MinimapIcons row", Probe(replay), StringComparison.Ordinal);
    }
}
