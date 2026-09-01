using System.Globalization;
using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// The stat browser's search, which answers "is this stat on the entity at all".
/// </summary>
/// <remarks>
/// Worth its own file because of what the answer is used for. A player's bag runs to four
/// hundred rows; the search exists so that seeing nothing MEANS something is not there. A
/// search that silently fails to match turns the browser from a reference into a source of
/// wrong conclusions - the tool would be saying "no such stat" about a stat it is holding.
/// </remarks>
public class EntityStatSearchTests
{
    /// <summary>A slice of a real player's bag, names and ids as the browser shows them.</summary>
    private static readonly EntityStat[] Bag =
    [
        new(2, 96, "level", "StatsByBuffAndActions"),
        new(1223, 53, "spell_damage_+%", "StatsByBuffAndActions"),
        new(21907, 4000, "display_cull_rare_life_%_threshold", "StatsByBuffAndActions"),
        new(240, 4365, "maximum_mana", "StatsByBuffAndActions"),
        new(13190, 0, "", "StatsByItems"),
        new(240, 4580, "maximum_mana", "StatsByItems"),
    ];

    [Fact]
    public void FindsTheStatByItsName()
    {
        // The case this was built for: the name is known, the list is four hundred rows, and
        // two passes by eye did not settle whether it was in there.
        EntityStat found = Assert.Single(EntityStats.Matching(Bag, "display_cull_rare"));
        Assert.Equal(4000, found.Value);
    }

    [Fact]
    public void FindsItWhateverCaseIsTyped()
        => Assert.Single(EntityStats.Matching(Bag, "DISPLAY_CULL_RARE"));

    [Fact]
    public void FindsAStatByItsIdAndByItsValue()
    {
        // An id is what a schema note carries, and a value is what somebody reads off a
        // tooltip and wants to find the source of. Both are ways people arrive at this box.
        Assert.Single(EntityStats.Matching(Bag, "21907"));
        Assert.Single(EntityStats.Matching(Bag, "4000"));
    }

    [Fact]
    public void KeepsBothBagsAndTheOrderTheyWereReadIn()
    {
        // The same stat sits in both bags with DIFFERENT values - the sheet's mana in one and
        // a smaller number in the other. A search that dropped one, or reordered them away
        // from their bag headings, would make every row ambiguous about where it came from.
        List<EntityStat> mana = EntityStats.Matching(Bag, "maximum_mana");

        Assert.Equal(2, mana.Count);
        Assert.Equal([4365, 4580], mana.Select(stat => stat.Value));
        Assert.Equal(["StatsByBuffAndActions", "StatsByItems"], mana.Select(stat => stat.Source));
    }

    [Fact]
    public void AnEmptySearchIsNotAFilter()
    {
        Assert.Equal(Bag.Length, EntityStats.Matching(Bag, "").Count);
        Assert.Equal(Bag.Length, EntityStats.Matching(Bag, null).Count);
    }

    [Fact]
    public void SomethingThatIsNotThereFindsNothing()
    {
        // The half that makes the search worth trusting. "No rows" has to mean "not in the
        // bag" - if this ever matched loosely, an absence could not be read off the screen.
        Assert.Empty(EntityStats.Matching(Bag, "display_cull_unique"));
        Assert.Empty(EntityStats.Matching(Bag, "culling_strike_threshold"));
    }

    [Fact]
    public void AnUnnamedStatIsStillReachableByItsId()
    {
        // Ids with no row in the game's table are shown as numbers, and those are exactly the
        // ones somebody is chasing when hunting an offset. They must not fall out of a search.
        EntityStat found = Assert.Single(EntityStats.Matching(Bag, "13190"));
        Assert.Empty(found.Name);
    }

    [Fact]
    public void TheSearchReadsTheSameInEveryLocale()
    {
        // Invariant formatting for the id and the value: on a machine whose culture groups
        // thousands, 4365 renders as "4.365" and a search for "4365" would find nothing.
        Assert.Equal("4365", 4365.ToString(null, CultureInfo.InvariantCulture));
        Assert.Single(EntityStats.Matching(Bag, "4365"));
    }
}
