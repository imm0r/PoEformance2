using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Features;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The league mechanic on a node, and the picture the GAME puts on it.
/// </summary>
/// <remarks>
/// WHAT THIS ANSWERS, and it is a fault this project caused. The atlas names a node's mechanic
/// with a content token - 26741 is <c>map_atlas_node_has_ritual</c> - and data/atlas-content.json
/// supplied a picture for those tokens off ids that were two rows short, so a Delirium node wore
/// a Ritual symbol. Dropping the file behind the game turned the wrong picture into NO picture,
/// because the game's stat description is a sentence and carries no art.
///
/// THE GAME HAS THE PICTURE, one link further on: node -> EndgameMapAtlas row -> an
/// EndgameMapObjectives row, which carries the whole art path beside the sentence. Nothing here
/// matches a token to a picture; the game's own link is followed.
///
/// WHAT THE COMMITTED CAPTURES CAN AND CANNOT SHOW. A recording holds only the reads its build
/// performed, and the builds that took these did not read every objective's art - so two of the
/// eight ids resolve here and the rest read as nothing. That is enough to settle the COLUMN,
/// which is the part that could have been wrong: both land on exactly the path the badge table
/// already names, from two different captures. Full coverage needs a capture taken with this
/// reader in place.
/// </remarks>
public class AtlasObjectiveSessionTests
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
            return dir!;
        }
    }

    private static (List<AtlasNode> Nodes, AtlasObjectiveCatalogue Objectives, ReplayMemoryReader Replay) Walked(
        string capture)
    {
        ReplayMemoryReader replay = ReplayMemoryReader.Load(
            File.OpenRead(Path.Combine(Root.FullName, "tests", "fixtures", capture)));
        OffsetSchema schema = RealSessionTests.LiveSchema();
        GameChainAddresses chain = GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]);

        List<AtlasNode> nodes = new AtlasReader(replay, schema, new UiElementReader(replay, schema))
            .Read(chain.UiRoot, new UiScale(3440, 1440, 0));
        Assert.True(nodes.Count > 100, $"only {nodes.Count} nodes read off the panel");

        return (nodes, new AtlasObjectiveCatalogue(replay, schema), replay);
    }

    [Theory]
    [InlineData("session-2026-09-atlastext.rec", "Abyss", "AtlasIconContentAbyss")]
    [InlineData("session-2026-09-mapcontent.rec", "Ritual", "AtlasIconContentRitual")]
    public void THEGAMESaysWhichMechanicANodeHostsAndWhatItLooksLike(
        string capture, string mechanic, string art)
    {
        (List<AtlasNode> nodes, AtlasObjectiveCatalogue objectives, ReplayMemoryReader replay) = Walked(capture);
        using (replay)
        {
            var icons = new SortedDictionary<string, string>(StringComparer.Ordinal);
            int hosting = 0;
            foreach (AtlasNode node in nodes)
            {
                if (objectives.For(node.Address) is { } found)
                {
                    hosting++;
                    icons[found.Id] = found.Icon;
                }
            }

            Assert.True(hosting > 50, $"only {hosting} nodes reached an objective");

            // THE COLUMN IS THE CLAIM. The art name it lands on is exactly the one the shipped
            // badge table carries for the same mechanic, reached by a completely different route -
            // which is what says +0x18 is the icon rather than merely a readable string.
            Assert.Equal(art, icons[mechanic]);
        }
    }

    [Fact]
    public void ANDTheMechanicComesOutAsAWordWithAPictureRatherThanABareNumber()
    {
        (List<AtlasNode> nodes, AtlasObjectiveCatalogue objectives, ReplayMemoryReader replay) =
            Walked("session-2026-09-mapcontent.rec");
        using (replay)
        {
            AtlasContentNames contents = AtlasContentNames.Load(
                Path.Combine(Root.FullName, "data", "atlas-content.json"));

            AtlasNode ritual = Assert.Single(
                nodes.Where(node => objectives.For(node.Address)?.Id == "Ritual").Take(1));
            AtlasObjective found = Assert.NotNull(objectives.For(ritual.Address));

            // THE GAME'S OWN WORDS, markup stripped the way everything else here strips it: the
            // row holds "Complete all [ContainsRitual|Ritual Altars]".
            Assert.Equal("Complete all Ritual Altars", found.Words);

            IReadOnlyList<AtlasSaid> said = AtlasWatch.Words(ritual, contents, found);
            AtlasSaid first = said[0];
            Assert.Equal("Complete all Ritual Altars", first.Text);
            Assert.Equal("AtlasIconContentRitual", first.Icon);

            // AND WITHOUT IT NOTHING CHANGES, which is what keeps this additive: a node with no
            // objective reads exactly as it did before.
            Assert.DoesNotContain(
                AtlasWatch.Words(ritual, contents),
                line => line.Icon == "AtlasIconContentRitual" && line.Text == found.Words);
        }
    }

    [Fact]
    public void ANDAROWThatWillNotReadIsAskedOnceRatherThanPerNode()
    {
        // 19 rows, 8 in use, and a study pass asks per NODE - so a row that reads as nothing has
        // to be remembered too, or a capture that cannot answer it pays for that on every node.
        (List<AtlasNode> nodes, AtlasObjectiveCatalogue objectives, ReplayMemoryReader replay) =
            Walked("session-2026-09-mapcontent.rec");
        using (replay)
        {
            foreach (AtlasNode node in nodes)
            {
                objectives.For(node.Address);
            }

            int once = objectives.Known;
            Assert.InRange(once, 1, 19);

            foreach (AtlasNode node in nodes)
            {
                objectives.For(node.Address);
            }

            Assert.Equal(once, objectives.Known);
        }
    }

    [Fact]
    public void THECheckRunsToTheENDOnAnAtlasThatHostsMechanics()
    {
        // THE TEST THAT WAS MISSING, and its absence shipped a crash. Everything else here
        // exercises the CATALOGUE; nothing ran Check itself, and Check is the only caller of the
        // block that summarises it. That block asked a dictionary of (int, string, string) for
        // GetValueOrDefault - whose default carries NULL strings - so the first node that hosted
        // anything dereferenced null and the whole check came back as one line:
        //
        //   the check itself failed: Object reference not set to an instance of an object.
        //
        // On every real atlas, because every real atlas has a mechanic somewhere. A unit test of
        // the reader could not have caught it; only running the thing the button runs could.
        ReplayMemoryReader replay = ReplayMemoryReader.Load(
            File.OpenRead(Path.Combine(Root.FullName, "tests", "fixtures", "session-2026-09-mapcontent.rec")));
        using (replay)
        {
            OffsetSchema schema = RealSessionTests.LiveSchema();
            var watch = new AtlasWatch(
                replay,
                schema,
                replay.ResolvedStatics["GameStates"],
                AtlasContentNames.Load(Path.Combine(Root.FullName, "data", "atlas-content.json")),
                AtlasMapNames.Load(Path.Combine(Root.FullName, "data", "atlas-maps.json")));

            watch.CheckTheRead();
            watch.Service(new UiScale(3440, 1440, 0), 0);

            IReadOnlyList<string> said = watch.Checked;
            Assert.NotEmpty(said);

            // THE ALARM, worded so it cannot pass by accident: whatever else the check says, it
            // must not say that it fell over.
            Assert.DoesNotContain(said, line => line.Contains("the check itself failed", StringComparison.Ordinal));

            // And it really did reach the block that crashed, rather than bailing out earlier.
            Assert.Contains(said, line => line.StartsWith("NODE MECHANICS", StringComparison.Ordinal));
            Assert.Contains(said, line => line.Contains("Ritual", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ANODEWithNoObjectiveIsTheOrdinaryCase()
    {
        (List<AtlasNode> nodes, AtlasObjectiveCatalogue objectives, ReplayMemoryReader replay) =
            Walked("session-2026-09-mapcontent.rec");
        using (replay)
        {
            int without = nodes.Count(node => objectives.For(node.Address) is null);
            Assert.True(without > nodes.Count / 2, $"only {without} of {nodes.Count} host nothing");

            // And an address that is not a node at all answers null rather than throwing.
            Assert.Null(objectives.For(0));
            Assert.Null(objectives.For(0xDEAD_BEEF));
        }
    }
}
