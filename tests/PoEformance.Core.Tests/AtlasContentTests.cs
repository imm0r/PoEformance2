using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Turning the numbers on an atlas node into something readable.
/// </summary>
/// <remarks>
/// The shipped table is ported from GameHelper2 - all 112 of its entries - and like every
/// other piece of game knowledge here it is DATA: a league renames things, and correcting a
/// line should not need a rebuild.
/// </remarks>
public class AtlasContentTests
{
    private static string DataFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "atlas-content.json")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "data", "atlas-content.json");
    }

    private static AtlasContentNames Loaded() => AtlasContentNames.Load(DataFile());

    [Fact]
    public void THELowHalfIsTheContentAndTheHighHalfIsHowMuch()
    {
        // The number on a node is not the number in the table. "3 additional Shrines" is one
        // id with a 3 above it, so looking the whole word up finds nothing at all.
        Assert.Equal(0x0963u, AtlasContentNames.IdOf(0x00030963u));
        Assert.Equal(3u, AtlasContentNames.MagnitudeOf(0x00030963u));

        Assert.Equal(0u, AtlasContentNames.MagnitudeOf(0x0065u));
    }

    [Fact]
    public void AKnownBadgeReadsAsItself()
    {
        AtlasContent breach = Assert.NotNull(Loaded().Badge(0x0065));
        Assert.Equal("Breach", breach.Name);
        Assert.Contains("Breach", breach.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ANDItStillDoesWithAMagnitudeOnTop()
    {
        AtlasContent shrines = Assert.NotNull(Loaded().Effect(0x00030963u));
        Assert.Contains("Shrines", shrines.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ANIdInBOTHTablesKeepsBothOfItsMeanings()
    {
        // 0x6157 is a badge and an effect in the reference, worded differently. Merging the
        // tables would silently relabel one of them, so they are kept apart here too.
        AtlasContentNames names = Loaded();

        AtlasContent badge = Assert.NotNull(names.Badge(0x6157));
        AtlasContent effect = Assert.NotNull(names.Effect(0x6157));

        Assert.Equal("Grand Mirror", badge.Name);
        Assert.Equal(string.Empty, effect.Name);
    }

    [Fact]
    public void ANUnknownIdIsNothingRatherThanAGuess()
    {
        Assert.Null(Loaded().Badge(0xDEAD));
        Assert.Null(Loaded().Effect(0xDEAD));
    }

    [Fact]
    public void AMissingFileLeavesTheAtlasReadableWithoutNames()
    {
        // A tool that will not start because a data file was deleted is worse than an atlas
        // whose contents show as bare numbers.
        AtlasContentNames none = AtlasContentNames.Load("nowhere/at/all.json");

        Assert.Equal(0, none.Count);
        Assert.Null(none.Badge(0x0065));
    }

    [Fact]
    public void THEShippedTableIsTheWholeReference()
    {
        // 69 badges, 43 effects, 41 older tokens: every entry the reference carries. A count
        // here is what catches an extraction that quietly dropped half a table.
        Assert.Equal(69 + 43 + 41, Loaded().Count);
    }

    [Fact]
    public void ANDTheLabelPrefersTheNameOverTheSentence()
    {
        AtlasContentNames names = Loaded();

        Assert.Equal("Breach", Assert.NotNull(names.Badge(0x0065)).Label);
        Assert.Contains(" ", Assert.NotNull(names.Effect(0x6157)).Label, StringComparison.Ordinal);
    }
}
