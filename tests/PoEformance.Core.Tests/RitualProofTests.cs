using PoEformance.Features;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Checking a prediction against what the game actually rolled.
/// </summary>
/// <remarks>
/// The one thing that can say whether ANY of the ritual prediction is right for this client -
/// the generator is proved to match the reference, and nothing proves the reference still
/// matches PoE2. So the part that has to be right here is the reconstruction: working out, for
/// a line already drawn, what the model SHOULD have said for each map on it.
///
/// That is the forward rule run backwards, and it has exactly the same trap: the rank is taken
/// among the maps not yet on the line, in position order. Getting it wrong reports a correct
/// model as broken, which is worse than not checking at all.
/// </remarks>
public class RitualProofTests
{
    private static List<RitualMod> Pool()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "ritual-mods.json")))
        {
            dir = dir.Parent;
        }

        return RitualMods.Load(Path.Combine(dir!.FullName, "data", "ritual-mods.json"))
            .Available(new Dictionary<int, int>());
    }

    private static RitualLineState Drawn(params (int X, int Y)[] committed)
    {
        // A little atlas where each map leads to the next two, so ranks are worth getting right.
        var candidates = new Dictionary<(int X, int Y), IReadOnlyList<(int X, int Y)>>
        {
            [(0, 0)] = [(1, 0), (1, 1)],
            [(1, 0)] = [(0, 0), (2, 0), (2, 1)],
            [(1, 1)] = [(0, 0), (2, 1)],
            [(2, 0)] = [(1, 0)],
            [(2, 1)] = [(1, 0), (1, 1)],
        };

        return new RitualLineState(77, committed, candidates, 0);
    }

    [Fact]
    public void THEStartIsNeverRolledAndIsNotJudged()
    {
        // Taking the first map only begins the line - the game rolls nothing for it, so a
        // check that expected a reward there would report a failure on every single line.
        IReadOnlyList<RitualProofRow> rows = RitualProof.Expected(Drawn((0, 0)), Pool(), ["whatever"]);

        RitualProofRow start = Assert.Single(rows);
        Assert.Equal(0, start.Position);
        Assert.Equal(string.Empty, start.Predicted);
        Assert.False(start.Comparable);
    }

    [Fact]
    public void EACHMapIsJudgedAgainstTheRollItsOwnPlaceWouldHaveMade()
    {
        // The reconstruction has to agree with the forward planner, or the check measures the
        // difference between two of my own routines rather than anything about the game. Same
        // line, planned forward and reconstructed backwards.
        List<RitualMod> pool = Pool();
        RitualLineState empty = Drawn();

        IReadOnlyList<RitualChain> plans = RitualPlanner.Plan(
            empty, new HashSet<(int X, int Y)>(), [(0, 0)], pool, length: 3);

        RitualChain plan = plans[0];
        (int X, int Y)[] walked = [plan.From, .. plan.Stops.Select(stop => stop.At)];

        IReadOnlyList<RitualProofRow> rows = RitualProof.Expected(
            Drawn(walked), pool, ["", "", ""]);

        Assert.Equal(3, rows.Count);
        for (int i = 1; i < rows.Count; i++)
        {
            // The planner keeps the short label; the check keeps the game's own wording, which
            // is what the game shows. So the check's text must CONTAIN what the plan shortened.
            Assert.Contains(plan.Stops[i - 1].First, RitualRewards.Short(rows[i].Predicted));
        }
    }

    [Fact]
    public void ATwoRewardMapAgreesWhenItsFirstRewardIsInWhatTheGameShows()
    {
        // The game shows both mods as one block of text. Demanding equality would report every
        // correct two-reward map as a failure.
        var row = new RitualProofRow(1, (1, 0), 0, "4 Exalted Orbs",
            "4 Exalted Orbs\nMonsters Sacrificed grant 25% increased Tribute");

        Assert.True(row.Comparable);
        Assert.True(row.Holds);
    }

    [Fact]
    public void ANDDiffersWhenItIsNotThere()
    {
        var row = new RitualProofRow(1, (1, 0), 0, "4 Exalted Orbs", "2 Chaos Orbs");

        Assert.True(row.Comparable);
        Assert.False(row.Holds);
    }

    [Fact]
    public void AMapWhoseTextCouldNotBeReadIsNotCountedEitherWay()
    {
        // A map the atlas has scrolled away from has no element to read. Counted as a
        // disagreement it would condemn a correct model for being off screen.
        var unread = new RitualProofRow(1, (1, 0), 0, "4 Exalted Orbs", string.Empty);
        Assert.False(unread.Comparable);
        Assert.False(unread.Holds);

        var unpredicted = new RitualProofRow(1, (1, 0), -1, string.Empty, "4 Exalted Orbs");
        Assert.False(unpredicted.Comparable);
    }

    [Fact]
    public void AMapReachedFromSomewhereWithNoCandidatesHasNoRank()
    {
        // Which happens when the candidate table has not loaded. Reported as "cannot say"
        // rather than as rank 0, which would roll a reward and judge against it.
        var line = new RitualLineState(
            5, [(0, 0), (9, 9)], new Dictionary<(int X, int Y), IReadOnlyList<(int X, int Y)>>(), 0);

        IReadOnlyList<RitualProofRow> rows = RitualProof.Expected(line, Pool(), ["", "something"]);

        Assert.Equal(-1, rows[1].Rank);
        Assert.Equal(string.Empty, rows[1].Predicted);
        Assert.False(rows[1].Comparable);
    }

    [Fact]
    public void NOTHINGIsRecordedUntilAMapHasActuallyRolled()
    {
        string path = Path.Combine(Path.GetTempPath(), $"proof-{Guid.NewGuid():N}.txt");
        try
        {
            var proof = new RitualProof { Enabled = true };

            // A line with a start and nothing rolled yet - which is most of the time a line is
            // on screen. Recording it would fill the file with states that say nothing.
            proof.Look(Drawn((0, 0)), Pool(), [string.Empty], path);

            Assert.Equal(0, proof.Recorded);
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ANDTheSameStateIsNotRecordedTwice()
    {
        // This is looked at every tick while a line is on screen, so without the check the file
        // fills with the same picture in about a minute.
        string path = Path.Combine(Path.GetTempPath(), $"proof-{Guid.NewGuid():N}.txt");
        try
        {
            var proof = new RitualProof { Enabled = true };
            RitualLineState line = Drawn((0, 0), (1, 0));
            List<RitualMod> pool = Pool();

            proof.Look(line, pool, ["", "4 Exalted Orbs"], path);
            proof.Look(line, pool, ["", "4 Exalted Orbs"], path);
            proof.Look(line, pool, ["", "4 Exalted Orbs"], path);

            Assert.Equal(1, proof.Recorded);

            // A line that has moved on IS a new state.
            proof.Look(Drawn((0, 0), (1, 0), (2, 0)), pool, ["", "4 Exalted Orbs", "2 Chaos Orbs"], path);
            Assert.Equal(2, proof.Recorded);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ANDNothingHappensAtAllWhileItIsSwitchedOff()
    {
        string path = Path.Combine(Path.GetTempPath(), $"proof-{Guid.NewGuid():N}.txt");
        try
        {
            var proof = new RitualProof();
            proof.Look(Drawn((0, 0), (1, 0)), Pool(), ["", "4 Exalted Orbs"], path);

            Assert.Equal(0, proof.Recorded);
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WHATItWroteSaysWhichMapDisagreed()
    {
        // The file is read by whoever ran the line, not by whoever wrote the model, so a
        // disagreement has to be findable without knowing how any of it works.
        string path = Path.Combine(Path.GetTempPath(), $"proof-{Guid.NewGuid():N}.txt");
        try
        {
            var proof = new RitualProof { Enabled = true };
            proof.Look(Drawn((0, 0), (1, 0)), Pool(), ["", "something the model will not have said"], path);

            string written = File.ReadAllText(path);
            Assert.Contains("predicted:", written);
            Assert.Contains("actual:", written);
            Assert.Contains("DIFFERS", written);
            Assert.Contains("the start - never rolled", written);
            Assert.Contains("DID NOT", proof.Last);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
