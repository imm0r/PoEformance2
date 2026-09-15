using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Turning the numbers on an atlas node into something readable.
/// </summary>
/// <remarks>
/// The shipped table is ported from GameHelper2 and, like every other piece of game knowledge
/// here, it is DATA: a league renames things, and correcting a line should not need a rebuild.
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
        Assert.Equal(0x0963u, AtlasContentNames.IdOf(0x00C00963u));
        Assert.Equal(3u, AtlasContentNames.MagnitudeOf(0x00C00963u));

        Assert.Equal(0u, AtlasContentNames.MagnitudeOf(0x0065u));
    }

    [Fact]
    public void ANDItCountsInSIXTYFOURTHSRatherThanInOnes()
    {
        // The one that put "x64" on every content on the atlas: a plain effect - one of the
        // thing - carries 64, not 1, so a high half taken literally reads as sixty-four of it.
        Assert.Equal(1u, AtlasContentNames.MagnitudeOf(0x00400963u));

        // And a binary effect ("always", "doubles") carries a hundred of them.
        Assert.Equal(100u, AtlasContentNames.MagnitudeOf(0x190061C7u));
    }

    [Fact]
    public void EXCEPTOnTheOneTokenWhoseHighHalfIsNotAllMagnitude()
    {
        // Delirious keeps two flags in the top of its high half. Left unmasked the same token
        // reads as tens of thousands of per cent, which is the sort of number nobody questions
        // on an atlas because nobody reads it twice.
        Assert.Equal(20u, AtlasContentNames.MagnitudeOf(0x8500685Au));
        Assert.Equal(20u, AtlasContentNames.MagnitudeOf(0x0500685Au));
    }

    [Fact]
    public void AWORDINGWithNoRoomForANumberIsNotGivenOne()
    {
        // "Area contains Abysses" says how many nowhere, so there is nothing to substitute -
        // and appending the magnitude anyway is what produced "Area contains Abysses x64".
        AtlasContentNames names = Loaded();

        Assert.Equal("Area contains Abysses", Assert.NotNull(names.Effect(0x00406872u)).Say(0x00406872u));
        Assert.Equal("Contains 3 additional Shrines", Assert.NotNull(names.Effect(0x00C00963u)).Say(0x00C00963u));
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
        // 69 badges and 43 effects. A count here is what catches an extraction that quietly
        // dropped half a table.
        Assert.Equal(69 + 43, Loaded().Count);
    }

    [Fact]
    public void ANDTheLegacyTokensItDroppedCouldNotHaveNamedAnything()
    {
        // WHY 41 ENTRIES WERE DELETED RATHER THAN KEPT IN CASE. The file used to carry a third
        // table, "legacyTokens", read as a fallback BEHIND the effects. Every one of its ids
        // was already an effect, and none of its 41 lines differed from the effect's own
        // wording, so the fallback was unreachable for every id in it - not unlikely to fire,
        // unable to.
        //
        // The test that says so cannot be written against the removed table, so it is written
        // against what the table claimed: the ids it covered still resolve, and they resolve to
        // the wording it would have supplied. If a future edit drops one of these from the
        // effects, this fails - which is the only thing the deleted table was ever protecting.
        AtlasContentNames names = Loaded();

        Assert.Equal("Contains 3 additional Shrines", Assert.NotNull(names.Effect(0x00C00963u)).Say(0x00C00963u));
        Assert.Equal("Map Boss drops a Unique item", Assert.NotNull(names.Effect(0x127Bu)).Label);
        Assert.Equal("Breach Hive Fortress", Assert.NotNull(names.Effect(0x3A5Eu)).Label);
        Assert.Equal("Area contains Breaches", Assert.NotNull(names.Effect(0x6875u)).Label);
    }

    [Fact]
    public void SIXTYSEVENOfTheBadgeIdsAreConsecutiveWhichIsWhatARowIndexLooksLike()
    {
        // THE PREMISE OF THE ONLY UNMEASURED THING LEFT IN THIS FILE, pinned on the file side where
        // it costs nothing. If badge ids are EndgameMapContent row indices plus 100 - which is what
        // AtlasNode.BadgeVectorBegin says the game does with that table - then the file's ids have
        // to be a run of consecutive numbers starting at 100, and they are: 0x64..0xA6 with no gap.
        //
        // The two that are NOT in that run are the interesting ones and are named here rather than
        // waved at. 0x3E8 and 0x6157 would be rows 900 and 24819 of a table nothing that small, and
        // 0x6157 is ALSO one of the file's effects with the same sentence - so if the effect ids
        // turn out to be Stats rows, those two were filed under the wrong heading by the port.
        uint[] ids = [.. Loaded().Badges.Keys.Order()];

        uint[] run = [.. ids.Where(id => id is >= 0x64 and <= 0xA6)];
        Assert.Equal(67, run.Length);
        Assert.Equal(Enumerable.Range(0x64, 67).Select(id => (uint)id), run);

        Assert.Equal<uint[]>([0x3E8, 0x6157], [.. ids.Where(id => id is < 0x64 or > 0xA6)]);
        Assert.Contains(0x6157u, Loaded().Effects.Keys);
    }

    [Fact]
    public void ANDTheEffectIdsAreNowhereNearThatRunButAreInStatsRange()
    {
        // The other half of the same premise: the effects cannot be the same table's rows. They run
        // 1240..26741 against a badge run that ends at 166, and Stats.dat has 27281 rows - which is
        // what makes "an effect id is a Stats row index" the hypothesis worth a capture.
        uint[] ids = [.. Loaded().Effects.Keys.Order()];

        Assert.Equal(43, ids.Length);
        Assert.Equal(1240u, ids[0]);
        Assert.Equal(26741u, ids[^1]);
        Assert.DoesNotContain(ids, id => id is >= 0x64 and <= 0xA6);
    }

    [Fact]
    public void ANDTheLabelPrefersTheNameOverTheSentence()
    {
        AtlasContentNames names = Loaded();

        Assert.Equal("Breach", Assert.NotNull(names.Badge(0x0065)).Label);
        Assert.Contains(" ", Assert.NotNull(names.Effect(0x6157)).Label, StringComparison.Ordinal);
    }
}
