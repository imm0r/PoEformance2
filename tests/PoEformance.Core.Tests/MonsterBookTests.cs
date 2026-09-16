using PoEformance.Features;
using PoEformance.Game.Entities;

namespace PoEformance.Core.Tests;

/// <summary>
/// The columns a grid draws, and the encoding it draws them with.
/// </summary>
/// <remarks>
/// AGAINST THE SHIPPED EXPORT, which is the point of the store living outside the window: 2733
/// real monsters are committed to this repository, so what a column holds, what a search matches
/// and how much of a bar a value earns are all things a test can settle rather than things
/// somebody has to squint at on a screen this machine does not have.
///
/// THE ALIGNMENT CHECK IS THE IMPORTANT ONE. A store is parallel arrays filled by one loop, and
/// the way that goes wrong is that one of them drifts by a row - which looks like nothing at all
/// until a monster is drawn with the next one's numbers. <see cref="EveryRowsNumbersAreItsOwn"/>
/// walks all 2733 rows against the table they came from, so a drift of one is a failure here
/// rather than a wrong answer on somebody's screen.
/// </remarks>
public class MonsterBookTests
{
    [Fact]
    public void TheScaleIsTheNinetiethPercentileAndWhatIsOverItIsCounted()
    {
        var values = new double[100];
        for (var at = 0; at < values.Length; at++)
        {
            values[at] = at + 1;
        }

        ColumnSpread spread = ColumnSpread.Of(values);

        Assert.Equal(91d, spread.Scale);
        Assert.Equal(9, spread.Over);
        Assert.Equal(1d, spread.Least);
        Assert.Equal(100d, spread.Most);

        Assert.Equal(1f, spread.Bar(91d));
        Assert.Equal(1f, spread.Bar(4000d));
        Assert.Equal(0f, spread.Bar(0d));
        Assert.Equal(0f, spread.Bar(-5d));
    }

    [Fact]
    public void AColumnWithNoSpreadEarnsNoBars()
    {
        ColumnSpread spread = ColumnSpread.Of([7d, 7d, 7d]);

        Assert.Equal(0d, spread.Scale);
        Assert.Equal(0f, spread.Bar(7d));
        Assert.Equal(0, spread.Over);
    }

    [Fact]
    public void AMostlyEmptyColumnFallsBackToItsLargest()
    {
        // p90 of this is zero, and the one row that carries anything is the one worth seeing.
        var values = new double[20];
        values[19] = 60d;

        ColumnSpread spread = ColumnSpread.Of(values);

        Assert.Equal(60d, spread.Scale);
        Assert.Equal(0.5f, spread.Bar(30d));
        Assert.Equal(0, spread.Over);
    }

    [Fact]
    public void EqualWordsSortEqual()
    {
        DataColumn words = DataColumn.Words("name", ["Skeletal Warrior", "Zombie", "skeletal warrior"]);

        // Ranked by position rather than densely, rows sharing a name would each sort differently
        // and the grid's own tie-break would never run - see HowManyMonstersShareAName for how
        // much of this table that is.
        Assert.Equal(0, words.Compare(0, 2));
        Assert.True(words.Compare(0, 1) < 0);
        Assert.True(words.Compare(1, 2) > 0);
    }

    [Fact]
    public void OnlyAMagnitudeEarnsABar()
    {
        DataColumn life = DataColumn.Magnitudes("life", "%", [100d, 200d], ["100%", "200%"]);
        Assert.True(life.Encoded);
        Assert.Equal(ColumnShape.Magnitude, life.Shape);

        // AttackCrit holds 0, 1 or 2 and names a KIND. A bar would say a 2 is twice a 1.
        DataColumn crit = DataColumn.Codes("crit", [0d, 2d], ["0", "2"]);
        Assert.False(crit.Encoded);
        Assert.Equal(ColumnShape.Kind, crit.Shape);
        Assert.True(crit.Compare(0, 1) < 0);

        DataColumn quest = DataColumn.RowNumbers("quest", [4211d, 12d], ["#4211", "#12"]);
        Assert.False(quest.Encoded);
        Assert.Equal(ColumnShape.Row, quest.Shape);
    }

    [Fact]
    public void WholeNumbersGetWholeBinsAndNothingIsBinnedAway()
    {
        // The modifier column's shape: 0 to 8, and twenty-four bins over it would leave eighteen
        // of them holding nothing - a comb, read as structure that is not there.
        double[] mods = [.. Enumerable.Range(0, 900).Select(at => (double)(at % 9))];
        ColumnSpread spread = ColumnSpread.Of(mods);

        Assert.True(spread.Width >= 1d, $"bin width {spread.Width} is narrower than the column's own step");
        Assert.Equal(9, spread.Bins.Length);
        Assert.Equal(900, spread.Bins.Sum());

        // The damage-spread column's shape: only ever multiples of ten. Binned in twos, twelve of
        // sixteen bins can never hold anything.
        double[] tens = [.. Enumerable.Range(0, 400).Select(at => (double)(at % 4 * 10))];
        ColumnSpread step = ColumnSpread.Of(tens);

        Assert.Equal(10d, step.Width);
        Assert.Equal(400, step.Bins.Sum());
        Assert.DoesNotContain(0, step.Bins);
    }

    [Fact]
    public void AColumnMostOfWhoseRowsWouldBeFullEarnsNoBars()
    {
        // Poise, as the export actually holds it: 2618 rows at 0.05 and a tail to 0.25. Scaled at
        // its own p90 that is a full bar on ninety-six percent of the table.
        var poise = new double[2733];
        Array.Fill(poise, 0.05d);
        for (var at = 0; at < 115; at++)
        {
            poise[at] = 0.05d + (0.001d * at);
        }

        ColumnSpread spread = ColumnSpread.Of(poise);

        Assert.Equal(0d, spread.Scale);
        Assert.Equal(0f, spread.Bar(0.25d));

        // The histogram is kept all the same: what is really in the column is still worth seeing.
        Assert.NotEmpty(spread.Bins);
        Assert.Equal(2733, spread.Bins.Sum());
    }

    [Fact]
    public void ADragAcrossTheHistogramOpensOutAtBothEnds()
    {
        double[] values = [.. Enumerable.Range(0, 1000).Select(at => (double)at)];
        ColumnSpread spread = ColumnSpread.Of(values);

        (double all, double allMost) = spread.Range(0d, 1d);
        Assert.Equal(0d, all);
        Assert.Equal(999d, allMost);

        // THE RIGHT-HAND BIN HOLDS THE TAIL the histogram stops short of - it ends at p99, not at
        // the largest value - so a drag into it has to reach the top of the column. Snapped to the
        // edge of the picture instead, selecting the last bar would drop the very rows it looks
        // like it is selecting.
        (double top, double topMost) = spread.Range(0.99d, 1d);
        Assert.Equal(999d, topMost);
        Assert.True(top < 999d, "the last bin should be a range and not one value");

        // And a drag that starts at the left edge reaches the bottom of the column.
        (double low, _) = spread.Range(0d, 0.1d);
        Assert.Equal(0d, low);
    }

    [Fact]
    public void EveryRangeHasToHold()
    {
        MonsterVarieties table = Shipped();
        MonsterBook book = MonsterBook.Of(table, null);
        var rows = new List<int>();

        int life = Index(book, "life");
        int skills = Index(book, "skills");

        book.Filter(null, [new ColumnRange(life, 200d, double.MaxValue)], rows);
        int tanky = rows.Count;
        Assert.InRange(tanky, 1, book.Count - 1);

        book.Filter(null, [new ColumnRange(skills, 10d, double.MaxValue)], rows);
        int busy = rows.Count;
        Assert.InRange(busy, 1, book.Count - 1);

        // TWO RANGES ARE TWO QUESTIONS ASKED AT ONCE - "the tanky ones that also have a lot of
        // skills" - so the answer can only be smaller than either.
        book.Filter(
            null,
            [new ColumnRange(life, 200d, double.MaxValue), new ColumnRange(skills, 10d, double.MaxValue)],
            rows);

        Assert.True(rows.Count <= Math.Min(tanky, busy), $"{rows.Count} is more than {tanky} and {busy}");

        foreach (int row in rows)
        {
            MonsterVariety one = Assert.IsType<MonsterVariety>(table.Find(book.Paths[row]));
            Assert.True(one.Life >= 200, book.Paths[row]);
            Assert.True(one.SkillCount >= 10, book.Paths[row]);
        }

        // A range on a column of words is ignored rather than refused - see MonsterBook.Filter.
        book.Filter(null, [new ColumnRange(Index(book, "name"), 5d, 6d)], rows);
        Assert.Equal(book.Count, rows.Count);
    }

    [Fact]
    public void EveryColumnIsOfferedSomewhereAndSixStartShowing()
    {
        MonsterVarieties table = Shipped();
        MonsterBook book = MonsterBook.Of(table, null);

        Assert.Equal(book.Store.Columns.Length, book.Groups.Length);
        Assert.Equal(book.Store.Columns.Length, book.Shown.Length);
        Assert.True(book.Store.Columns.Length > 20, $"only {book.Store.Columns.Length} columns to choose from");

        foreach (string group in book.Groups)
        {
            Assert.False(string.IsNullOrWhiteSpace(group));
        }

        // The six the window always had. A table that opened with all twenty-nine would be
        // unreadable, and the chooser exists so that somebody adds the two they are asking about.
        Assert.Equal(6, book.Shown.Count(one => one));
        Assert.True(book.Shown[0], "the first column carries the selection and cannot be hidden");

        // Every column's text is filled for every row: a column declared and never written would
        // be a null the first draw trips over.
        foreach (DataColumn column in book.Store.Columns)
        {
            Assert.Equal(book.Count, column.Text.Length);
            Assert.All(column.Text, Assert.NotNull);
        }
    }

    [Fact]
    public void HowManyMonstersShareAName()
    {
        // WHY EVERY ROW GETS ITS OWN ID, measured rather than asserted - which is the whole reason
        // this test exists. The comment this replaces said the table held "nineteen monsters called
        // Skeletal Warrior"; it holds exactly ONE, and searching for that name on a live client
        // returns six rows across three different names. The number had been carried from comment
        // to comment without anybody counting, which is how a justification outlives its fact.
        //
        // The real figures are far stronger than the invented one, so the rule stands: over the
        // shipped export 2225 of 2709 named rows share a name, 510 names repeat, and "Daemon" is on
        // 305 rows. Asserted loosely, because the export is regenerated from a live install and a
        // test that pinned the exact counts would fail on patch day for no reason at all.
        MonsterVarieties table = Shipped();

        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach ((_, MonsterVariety one) in table.All)
        {
            if (one.Name is { Length: > 0 } named)
            {
                byName[named] = byName.GetValueOrDefault(named) + 1;
            }
        }

        Assert.Equal(1, byName.GetValueOrDefault("Skeletal Warrior"));

        int repeated = byName.Values.Count(count => count > 1);
        int rows = byName.Values.Where(count => count > 1).Sum();
        int worst = byName.Values.Max();

        Assert.True(repeated > 100, $"only {repeated} names repeat, so the rule may not be needed");
        Assert.True(rows > 1000, $"only {rows} rows share a name");
        Assert.True(worst > 100, $"the commonest name is on only {worst} rows");
    }

    [Fact]
    public void AColumnKnowsItsWidestCell()
    {
        // What a clipped table cannot fit a column to: only forty rows are ever submitted, so the
        // width has to come from the column rather than from what happens to be on screen.
        DataColumn life = DataColumn.Magnitudes("life", "%", [100d, 2600d, 5d], ["100%", "2600%", "5%"]);
        Assert.Equal("2600%", life.Widest);

        Assert.Equal(string.Empty, DataColumn.Words("name", []).Widest);
    }

    [Fact]
    public void ColumnsHaveToAgreeAboutHowManyRowsThereAre()
    {
        Assert.Throws<ArgumentException>(() => DataColumn.Magnitudes("life", "%", [1d, 2d], ["1"]));

        Assert.Throws<ArgumentException>(() => ColumnStore.Of(
            DataColumn.Words("a", ["one", "two"]),
            DataColumn.Words("b", ["one"])));

        Assert.Equal(0, ColumnStore.Of().Rows);
    }

    [Fact]
    public void AnEmptyTableGivesAnEmptyBook()
    {
        MonsterBook book = MonsterBook.Of(MonsterVarieties.Empty, null);

        Assert.Equal(0, book.Count);
        Assert.Equal(-1, book.Row("Metadata/Monsters/Whatever"));
        Assert.Equal(-1, MonsterBook.Of(null, null).Row(null));
    }

    [Fact]
    public void EveryMonsterIsARowAndEveryRowIsFindable()
    {
        MonsterVarieties table = Shipped();
        MonsterBook book = MonsterBook.Of(table, null);

        Assert.Equal(table.Count, book.Count);
        Assert.Equal(table.Count, book.Store.Rows);

        for (var row = 0; row < book.Count; row++)
        {
            Assert.Equal(row, book.Row(book.Paths[row]));
        }

        // THE SPELLING AN ENTITY CARRIES, rather than the one the table is keyed in: a backslash
        // and an @variant suffix are both real, and MonsterVarieties.Same settles both. A book
        // keyed more strictly than the table would report a monster the table plainly holds as
        // missing, which reads as a gap in the data rather than as a refused spelling.
        string path = book.Paths[0];
        Assert.Equal(0, book.Row(path.Replace('/', '\\') + "@12"));
        Assert.Equal(0, book.Row(path.ToUpperInvariant()));
    }

    [Fact]
    public void EveryRowsNumbersAreItsOwn()
    {
        MonsterVarieties table = Shipped();
        MonsterBook book = MonsterBook.Of(table, null);

        DataColumn life = Column(book, "life");
        DataColumn damage = Column(book, "dmg");
        DataColumn skills = Column(book, "skills");
        DataColumn mods = Column(book, "mods");

        for (var row = 0; row < book.Count; row++)
        {
            MonsterVariety one = Assert.IsType<MonsterVariety>(table.Find(book.Paths[row]));

            Assert.Equal(one.Life, (int)life.Number[row]);
            Assert.Equal(one.Damage, (int)damage.Number[row]);
            Assert.Equal(one.SkillCount, (int)skills.Number[row]);
            Assert.Equal(one.Boss, book.Boss[row]);

            // EVERY MODIFIER ROW COUNTS, including the ones that resolve to nothing - a book that
            // counted only the ones it could name would show four where a monster has seven.
            int carried = (one.Mods?.Count ?? 0) + (one.Mods2?.Count ?? 0) + (one.SpecialMods?.Count ?? 0);
            Assert.Equal(carried, (int)mods.Number[row]);
        }
    }

    [Fact]
    public void ATagSearchFindsTheMonstersThatCarryIt()
    {
        MonsterVarieties table = Shipped();
        MonsterBook book = MonsterBook.Of(table, null);
        var rows = new List<int>();

        book.Filter(null, rows);
        Assert.Equal(book.Count, rows.Count);

        book.Filter("   ", rows);
        Assert.Equal(book.Count, rows.Count);

        // A tag a real monster really carries, taken out of the table rather than assumed - the
        // export is regenerated from a live install, so a hard-coded one would be a test that
        // fails on patch day for no reason.
        (string path, string tag) = FirstTag(table);
        book.Filter(tag, rows);

        Assert.Contains(book.Row(path), rows);

        // AND NOTHING IT FINDS IS UNACCOUNTED FOR. Checked against the TABLE's own joins rather
        // than against the text the filter just read, which would only prove that Contains works.
        foreach (int row in rows)
        {
            Assert.True(Carries(table, book.Paths[row], tag), book.Paths[row]);
        }
    }

    [Fact]
    public void TheFlagWordsAreSearchableAtAll()
    {
        MonsterVarieties table = Shipped();
        MonsterBook book = MonsterBook.Of(table, null);
        var rows = new List<int>();

        // "boss" is a flag on the row rather than a column or a tag, so without the word being
        // written into the searchable text there is no way whatever to ask this list for them.
        int boss = Array.IndexOf(book.Boss, true);
        Assert.True(boss >= 0, "the export should carry bosses, and carries none");

        book.Filter("boss", rows);
        Assert.Contains(boss, rows);
    }

    [Fact]
    public void TheLifeColumnSpendsItsBarWhereTheMonstersAre()
    {
        MonsterVarieties table = Shipped();
        MonsterBook book = MonsterBook.Of(table, null);
        ColumnSpread spread = Column(book, "life").Spread;

        float low = spread.Bar(spread.Percentile(0.10d));
        float high = spread.Bar(spread.Percentile(0.90d));

        // THE CHECK THE ENCODING EXISTS TO PASS, and the one the obvious scalings fail. Scaled to
        // the largest value, the middle eighty percent of this column sits between 4% and 10% of
        // the bar: a column of stubs that says nothing about the rows somebody is scrolling past.
        Assert.True(
            high - low > 0.5f,
            $"the middle 80% of life occupies {high - low:P0} of the bar, scaled at {spread.Scale}");

        // And the tail really is long enough for that to have been the choice it was: the largest
        // life multiplier in the table is several times the value at which the bar fills.
        Assert.True(spread.Most / spread.Scale > 4d, $"{spread.Most} against a scale of {spread.Scale}");
        Assert.True(spread.Over > 0, "and something is above the scale, or there was nothing to clamp");
    }

    /// <summary>The shipped export, found by walking up to the repository root.</summary>
    private static MonsterVarieties Shipped()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "monster-varieties.json")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        MonsterVarieties table = MonsterVarieties.Load(
            Path.Combine(dir!.FullName, "data", "monster-varieties.json"));

        Assert.True(table.Count > 2000, $"the export should hold the whole table, and holds {table.Count}");
        return table;
    }

    private static DataColumn Column(MonsterBook book, string name)
        => Assert.Single(book.Store.Columns, one => string.Equals(one.Name, name, StringComparison.Ordinal));

    private static int Index(MonsterBook book, string name)
    {
        for (var at = 0; at < book.Store.Columns.Length; at++)
        {
            if (string.Equals(book.Store.Columns[at].Name, name, StringComparison.Ordinal))
            {
                return at;
            }
        }

        Assert.Fail($"the book has no column called {name}");
        return -1;
    }

    /// <summary>A monster that carries a tag worth searching for, and the tag.</summary>
    private static (string Path, string Tag) FirstTag(MonsterVarieties table)
    {
        foreach ((string path, MonsterVariety one) in table.All)
        {
            foreach (string tag in table.TagsOf(one))
            {
                // Long enough that it is not a substring of half the table by accident.
                if (tag.Length >= 6)
                {
                    return (path, tag);
                }
            }
        }

        Assert.Fail("the export carries no named tags at all");
        return (string.Empty, string.Empty);
    }

    /// <summary>Whether the table itself can account for this word being on this monster.</summary>
    private static bool Carries(MonsterVarieties table, string path, string word)
    {
        if (path.Contains(word, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (table.Find(path) is not { } one)
        {
            return false;
        }

        if (one.Name is { Length: > 0 } named && named.Contains(word, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (table.Kind(one)?.Id is { Length: > 0 } kind && kind.Contains(word, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (table.BloodName(one).Contains(word, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Any(table.TagsOf(one), word) || Any(table.Skills(one), word) || Any(table.ResistancesOf(one), word))
        {
            return true;
        }

        foreach (int row in Rows(one))
        {
            if (table.Modifier(row) is not { } mod)
            {
                continue;
            }

            if (mod.Id.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (ModifierStat stat in mod.Stats ?? [])
            {
                if (stat.Stat.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool Any(IEnumerable<string> said, string word)
        => said.Any(one => one.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<int> Rows(MonsterVariety one)
        => (one.Mods ?? []).Concat(one.Mods2 ?? []).Concat(one.SpecialMods ?? []);
}
