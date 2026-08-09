using PoEformance.Game.Items;

namespace PoEformance.Core.Tests;

/// <summary>
/// Turning the numbers on an item into what the game says about it.
/// </summary>
/// <remarks>
/// The shipped tables come from the game's own data, extracted by the AHK tool's scripts. What
/// is checked here is the LOOKUP: that a stat memory holds as a row number comes back as words,
/// that an unknown one still says something true, and that a line built from two stats does not
/// have this stat's number put in the other's place.
/// </remarks>
public class ItemNamesTests
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

    private static ItemNames Loaded()
        => ItemNames.Load(
            DataFile("item-stats.json"), DataFile("item-names.json"), DataFile("unique_ivi_name_map.tsv"));

    [Fact]
    public void THEShippedTablesCoverWhatAnItemCanCarry()
    {
        (int stats, int mods, int bases, int uniques) = Loaded().Counts;

        Assert.Equal(27_000, stats);
        Assert.True(mods > 6_000, $"only {mods} mods");
        Assert.True(bases > 5_000, $"only {bases} base types");
        Assert.True(uniques > 400, $"only {uniques} uniques");
    }

    [Fact]
    public void AUNIQUEIsNamedFromTheIdRatherThanFromItsPath()
    {
        ItemNames names = Loaded();

        // The id is what memory holds, and it stays English on a localised client where the
        // painted name does not - which is the whole reason this table is keyed by it.
        Assert.Equal("Astramentis", names.Unique("FourUniqueAmulet15"));
        Assert.Equal("Tabula Rasa", names.Unique("FourUniqueBodyStrDexInt1"));
        Assert.Equal("Morior Invictus", names.Unique("FourUniquePinnacle1"));
        Assert.Equal(string.Empty, names.Unique("SomethingAddedThisLeague"));
        Assert.Equal(string.Empty, names.Unique(null));
    }

    [Fact]
    public void ANDTheCommentedHeaderOfTheUniqueTableIsNotAUnique()
    {
        // The file opens with "#" lines describing where it came from, and the last of them is
        // "# columns: ivi_id<tab>unique_name" - which splits into a perfectly well-formed row.
        // Read as data it is a unique called "unique_name".
        Assert.Equal(string.Empty, Loaded().Unique("# columns: ivi_id"));
    }

    [Fact]
    public void ASTATComesBackAsTheGamesOwnWording()
    {
        ItemNames names = Loaded();

        // Found by its id rather than hard-coding a row number, because the row numbers are
        // the game's and shift between leagues - which is exactly why they are data.
        StatMeaning life = Enumerable.Range(0, 27_000)
            .Select(names.Stat)
            .First(stat => stat.Id == "base_maximum_life" && stat.Text.Length > 0);

        Assert.Contains("79", life.Say(79));
        Assert.DoesNotContain("{", life.Say(79));
    }

    [Fact]
    public void ANDTheSignIsPartOfTheWordingRatherThanDecoration()
    {
        // A thousand of the game's own lines write their number as "{0:+d}", which is where
        // the plus in "+79 to maximum Life" comes from. Replacing only the bare "{0}" leaves
        // every one of them untouched, reading as though the table had no wording for them.
        var signed = new StatMeaning("base_maximum_life", "{0:+d} to maximum Life", 0);

        Assert.Equal("+79 to maximum Life", signed.Say(79));
        Assert.Equal("-4 to maximum Life", signed.Say(-4));

        var plain = new StatMeaning("something", "{0:d} second between Triggers", 0);
        Assert.Equal("3 second between Triggers", plain.Say(3));
    }

    [Fact]
    public void ANDEveryShippedWordingCanActuallyBeFilled()
    {
        // The check that catches a format nobody anticipated: whatever placeholder styles the
        // game's data uses, a stat's OWN one must come out filled. Anything left over belongs
        // to the other half of a two-stat line, which is why only {N} for this stat is looked
        // for rather than any brace at all.
        ItemNames names = Loaded();
        var unfilled = new List<string>();

        for (int row = 0; row < 27_000; row++)
        {
            StatMeaning stat = names.Stat(row);
            if (stat.Text.Length == 0)
            {
                continue;
            }

            string said = stat.Say(42);
            if (said.Contains($"{{{stat.Argument}}}", StringComparison.Ordinal)
                || said.Contains($"{{{stat.Argument}:", StringComparison.Ordinal))
            {
                unfilled.Add(stat.Text);
            }
        }

        Assert.Empty(unfilled.Take(5));
    }

    [Fact]
    public void ANDAStatNobodyWroteDownStillSaysSomethingTrue()
    {
        // An unworded stat is still a fact about the item, and "maximum_life 79" is a great
        // deal more use than a blank row - especially to somebody reverse-engineering.
        var bare = new StatMeaning("some_new_stat", string.Empty, 0);
        Assert.Equal("some_new_stat 79", bare.Say(79));

        // And a row the table has never heard of keeps its number, which is the only thing
        // known about it.
        StatMeaning missing = ItemNames.Empty.Stat(999_999);
        Assert.Equal("stat #999999", missing.Id);
        Assert.Equal("stat #999999 5", missing.Say(5));
    }

    [Fact]
    public void ANDALineBuiltFromTwoStatsOnlyTakesItsOwnHalf()
    {
        // Some lines are filled by two stats - a minimum and a maximum. Filling both from one
        // of them would write the same number twice and read as a correct sentence.
        var high = new StatMeaning("damage_max", "Adds {0} to {1} Fire Damage", 1);

        Assert.Equal("Adds {0} to 24 Fire Damage", high.Say(24));

        var low = new StatMeaning("damage_min", "Adds {0} to {1} Fire Damage", 0);
        Assert.Equal("Adds 7 to {1} Fire Damage", low.Say(7));
    }

    [Fact]
    public void AMODIsCalledWhatTheGameCallsIt()
    {
        ItemNames names = Loaded();

        ModMeaning brute = names.Mod("Strength1");
        Assert.Equal("of the Brute", brute.Name);
        Assert.Equal("suffix", brute.Kind);
    }

    [Fact]
    public void ANDOneNobodyWroteDownKeepsItsIdWhichIsReadableEnough()
    {
        ModMeaning unknown = Loaded().Mod("SomethingNewNextLeague");

        Assert.Equal(string.Empty, unknown.Name);
        Assert.Equal(string.Empty, unknown.Kind);
    }

    [Fact]
    public void ABASETypeIsNamedAndFallsBackToItsPath()
    {
        ItemNames names = Loaded();

        Assert.Equal("Blacksmith's Whetstone", names.Base("Metadata/Items/Currency/CurrencyWeaponQuality"));

        // A league adding a base type gets an ugly row rather than a blank one.
        Assert.Equal("SomeNewThing", names.Base("Metadata/Items/Armours/SomeNewThing"));
        Assert.Equal(string.Empty, names.Base(null));
    }

    [Fact]
    public void ANDAMissingTableIsQuietRatherThanFatal()
    {
        // Without them the items still list - with raw paths, mod ids and stat numbers, which
        // is worse to read and just as true.
        ItemNames none = ItemNames.Load(null, null, null);

        Assert.Equal((0, 0, 0, 0), none.Counts);
        Assert.Equal("Boots", none.Base("Metadata/Items/Armours/Boots"));
        Assert.Equal(string.Empty, none.Unique("FourUniqueAmulet15"));
    }
}
