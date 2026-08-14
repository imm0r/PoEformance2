using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// What each map is worth running - the one file here that is an opinion.
/// </summary>
/// <remarks>
/// The risk this carries is not that a rating is wrong, which is a matter of taste. It is that
/// a rating never ARRIVES: the file is written by display name and the atlas works in internal
/// ids, so a name that resolves to nothing is a line somebody wrote that silently does nothing.
/// Most of these are about that.
/// </remarks>
public class AtlasRatingTests
{
    private static string DataFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", name)))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "data", name);
    }

    private static AtlasMapNames Names() => AtlasMapNames.Load(DataFile("atlas-maps.json"));

    private static AtlasRatings Shipped() => AtlasRatings.Load(DataFile("atlas-ratings.json"), Names());

    [Fact]
    public void ANAMEIsResolvedToEVERYIdThatCarriesIt()
    {
        // Several maps share a name - three different ids are all "Abyssal Depths" - and rating
        // the name means rating all of them, which is what somebody writing one line meant.
        AtlasMapNames names = Names();
        var wanted = new Dictionary<string, int> { ["Abyssal Depths"] = 7 };

        AtlasRatings ratings = AtlasRatings.Resolve(wanted, names);

        int carrying = names.All.Count(map => map.Value.Name == "Abyssal Depths");
        Assert.True(carrying > 1, "the fixture needs a shared name to be about anything");
        Assert.Equal(carrying, ratings.Count);
    }

    [Fact]
    public void ANDMatchedByOURNameRatherThanTheGamesOwn()
    {
        // The rule this project keeps hitting. The name in the file is reconciled against our
        // table of English names, which is the same on every client - not against anything the
        // game translates. What arrives at the atlas is an id, which is language-proof.
        AtlasRatings ratings = AtlasRatings.Resolve(
            new Dictionary<string, int> { ["Lost Towers"] = 3 }, Names());

        Assert.Equal(3, ratings.Of("MapLostTowers"));
        Assert.Null(ratings.Of("MapAugury"));
    }

    [Fact]
    public void ANAMEThatMatchesNothingIsREPORTEDRatherThanDropped()
    {
        // A typo is otherwise a rating that never appears, which is indistinguishable from
        // having forgotten to write it.
        AtlasRatings ratings = AtlasRatings.Resolve(
            new Dictionary<string, int> { ["Lost Towers"] = 3, ["Lost Tower"] = 9 }, Names());

        Assert.Equal("Lost Tower", Assert.Single(ratings.Unmatched));
        Assert.Equal(1, ratings.Count);
    }

    [Fact]
    public void THEScaleComesFromTheFileRatherThanFromANumberInTheCode()
    {
        // So any scale works: rate out of five and five is the green end. It is a RELATIVE
        // scale, which is the right way round for an opinion - green should mean "the best
        // there is" rather than "reached an arbitrary ten".
        AtlasRatings outOfFive = AtlasRatings.Resolve(
            new Dictionary<string, int> { ["Lost Towers"] = 5, ["Augury"] = 1 }, Names());

        Assert.Equal(5, outOfFive.Best);

        // And an unrated file has no scale rather than a scale of nought, so a caller cannot
        // divide by it without noticing.
        Assert.Equal(0, AtlasRatings.Empty.Best);
    }

    [Fact]
    public void AMissingFileLeavesTheAtlasWorkingWithoutRatings()
    {
        AtlasRatings none = AtlasRatings.Load("nowhere/at/all.json", Names());

        Assert.Equal(0, none.Count);
        Assert.Null(none.Of("MapLostTowers"));
    }

    [Fact]
    public void THEShippedFileResolvesCompletely()
    {
        // The one that earns its keep over time. A map renamed by a league, or a typo in an
        // edit, turns a rating into a line that does nothing - and nothing else would say so.
        AtlasRatings shipped = Shipped();

        Assert.Empty(shipped.Unmatched);
        Assert.True(shipped.Count >= 83, $"only {shipped.Count} ids rated");
        Assert.Equal(10, shipped.Best);
    }
}
