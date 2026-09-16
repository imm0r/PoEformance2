using PoEformance.Features;
using PoEformance.Game.Entities;

namespace PoEformance.Core.Tests;

/// <summary>
/// The filter grammar, the bitsets under it, and the facet counts they make possible.
/// </summary>
/// <remarks>
/// THE QUERY IS CHECKED AGAINST WALKING THE TABLE, which is the only check worth having here: an
/// index answers in microseconds what a foreach answers in milliseconds, and the whole risk is that
/// the fast answer is a different answer. Every case in <see cref="AQueryAgreesWithWalkingTheTable"/>
/// is a query and a plain predicate over MonsterVarieties that must pick the same 2733 rows.
///
/// AND THE ROUND-TRIP IS NOT ENOUGH ON ITS OWN, which this file learned the hard way: "life&gt;500"
/// was being parsed as "at least 500", written back as "&gt;=500", re-parsed to the same wrong thing
/// and declared correct. A round-trip only proves the writer and the reader agree with each other.
/// <see cref="GreaterThanIsNotAtLeast"/> is the check that would have caught it, and it is worth 984
/// rows on the commonest query anybody types.
/// </remarks>
public class ColumnQueryTests
{
    [Fact]
    public void ABitsetCountsOnlyTheRowsItIsOver()
    {
        // THE ONE PLACE A BITSET GOES WRONG QUIETLY: 100 rows is two 64-bit words, and the 28 bits
        // past the end are not rows of anything. A count that includes them is too high by a number
        // with no explanation.
        var rows = new RowSet(100);
        rows.All();
        Assert.Equal(100, rows.Count);

        rows.Not();
        Assert.Equal(0, rows.Count);

        rows.Add(7);
        rows.Add(99);
        rows.Add(100);
        Assert.Equal(2, rows.Count);
        Assert.True(rows.Has(7));
        Assert.False(rows.Has(100));

        var other = new RowSet(100);
        other.Add(99);
        Assert.Equal(1, rows.CountAnd(other));

        var into = new List<int>();
        rows.CopyTo(into);
        Assert.Equal([7, 99], into);

        rows.And(other);
        Assert.Equal([99], Listed(rows));

        rows.Or(other);
        Assert.Equal([99], Listed(rows));
    }

    [Fact]
    public void ABareWordStillJustSearches()
    {
        QueryResult result = ColumnQuery.Parse("undead");

        Assert.True(result.Ok);
        Assert.Equal(QueryKind.Word, result.Term!.Kind);
        Assert.Equal("undead", result.Term.Text);

        // An empty box is not an error and is not a term: it takes everything.
        Assert.False(ColumnQuery.Parse("   ").Ok);
        Assert.Equal(string.Empty, ColumnQuery.Parse("   ").Error);
    }

    [Fact]
    public void KeywordsEndWhereWordsEnd()
    {
        // "notorious" is a word somebody might type, not a negation of "orious".
        QueryResult notorious = ColumnQuery.Parse("notorious");
        Assert.True(notorious.Ok);
        Assert.Equal(QueryKind.Word, notorious.Term!.Kind);

        QueryResult negated = ColumnQuery.Parse("not orious");
        Assert.True(negated.Ok);
        Assert.Equal(QueryKind.Not, negated.Term!.Kind);
    }

    [Fact]
    public void WhatGoesWrongSaysWhereItWent()
    {
        QueryResult missing = ColumnQuery.Parse("life>");
        Assert.False(missing.Ok);
        Assert.Contains("number", missing.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(missing.Column > 0, "a caret needs a column to be drawn under");

        QueryResult unclosed = ColumnQuery.Parse("tag:undead (");
        Assert.False(unclosed.Ok);
        Assert.True(unclosed.Column > 0);

        QueryResult empty = ColumnQuery.Parse("tag:");
        Assert.False(empty.Ok);
    }

    [Theory]
    [InlineData("undead")]
    [InlineData("tag:undead")]
    [InlineData("tag:undead boss")]
    [InlineData("life>=120")]
    [InlineData("life>120")]
    [InlineData("life<120")]
    [InlineData("life 120..260")]
    [InlineData("tag:undead or tag:construct")]
    [InlineData("not boss")]
    [InlineData("(tag:undead or tag:beast) life>300")]
    [InlineData("crit=2")]
    [InlineData("tag:undead not skill:fire")]
    public void AQueryWritesBackOutAsItself(string text)
    {
        QueryResult first = ColumnQuery.Parse(text);
        Assert.True(first.Ok, first.Error);

        string written = ColumnQuery.Write(first.Term);
        QueryResult again = ColumnQuery.Parse(written);
        Assert.True(again.Ok, $"'{written}' would not read back: {again.Error}");

        // Written the same the second time round, which is what makes the rail and the box two
        // views of one filter rather than two stores that drift.
        Assert.Equal(written, ColumnQuery.Write(again.Term));
    }

    [Fact]
    public void GreaterThanIsNotAtLeast()
    {
        MonsterVarieties table = Shipped();
        MonsterBook book = MonsterBook.Of(table, null);

        int above = Rows(book, "life>100");
        int atLeast = Rows(book, "life>=100");
        int exactly = table.All.Count(one => one.Value.Life == 100);

        Assert.True(exactly > 0, "the export should hold monsters at exactly 100 life");
        Assert.Equal(atLeast - above, exactly);

        // The same the other way up, because the mistake is as easy to make there.
        Assert.Equal(Rows(book, "life<=100") - Rows(book, "life<100"), exactly);
    }

    [Fact]
    public void AQueryAgreesWithWalkingTheTable()
    {
        MonsterVarieties table = Shipped();
        MonsterBook book = MonsterBook.Of(table, null);

        Same(table, book, "tag:undead", one => Has(table.TagsOf(one), "undead"));
        Same(table, book, "life>200", one => one.Life > 200);
        Same(table, book, "life>=200", one => one.Life >= 200);
        Same(table, book, "life 120..260", one => one.Life is >= 120 and <= 260);
        Same(table, book, "skills>=10", one => one.SkillCount >= 10);
        Same(table, book, "crit=2", one => one.Crit == 2);
        Same(table, book, "skill:fire", one => Has(table.Skills(one), "fire"));

        Same(
            table,
            book,
            "tag:undead life>200",
            one => Has(table.TagsOf(one), "undead") && one.Life > 200);

        Same(
            table,
            book,
            "tag:undead or crit=2",
            one => Has(table.TagsOf(one), "undead") || one.Crit == 2);

        Same(table, book, "not tag:undead", one => !Has(table.TagsOf(one), "undead"));

        Same(
            table,
            book,
            "(tag:undead or tag:beast) life>300",
            one => (Has(table.TagsOf(one), "undead") || Has(table.TagsOf(one), "beast")) && one.Life > 300);
    }

    [Fact]
    public void AFieldThatIsNotThereSaysSo()
    {
        MonsterBook book = MonsterBook.Of(Shipped(), null);

        QueryResult parsed = ColumnQuery.Parse("bogus:thing");
        Assert.True(parsed.Ok, "it parses - only the table can know the field is not there");

        Assert.Null(book.Matching(parsed.Term, out string error));
        Assert.Contains("bogus", error, StringComparison.Ordinal);
    }

    [Fact]
    public void FacetsCountWhatIsActuallyLeft()
    {
        MonsterVarieties table = Shipped();
        MonsterBook book = MonsterBook.Of(table, null);

        RowSet within = Assert.IsType<RowSet>(
            book.Matching(ColumnQuery.Parse("tag:undead").Term, out _));

        var facets = new List<Facet>();
        book.Facets(within, "tag", facets);

        Assert.NotEmpty(facets);

        // MOST FIRST, so that a rail showing a dozen of five thousand shows the useful dozen.
        for (var at = 1; at < facets.Count; at++)
        {
            Assert.True(facets[at - 1].Count >= facets[at].Count, "the facets are not in order");
        }

        // AND EVERY COUNT IS WHAT WALKING THE TABLE WOULD SAY. This is the check the bitsets exist
        // to make cheap, so it is the one worth making expensively here.
        var rows = new List<int>();
        within.CopyTo(rows);

        foreach (Facet facet in facets)
        {
            int walked = rows.Count(row =>
                table.Find(book.Paths[row]) is { } one
                && table.TagsOf(one).Any(tag => string.Equals(tag, facet.Value, StringComparison.Ordinal)));

            Assert.Equal(walked, facet.Count);
        }

        // The tag everything in this set has must cover all of it.
        Assert.Equal(within.Count, facets[0].Count);

        book.Facets(within, "tag", facets, 3);
        Assert.Equal(3, facets.Count);

        book.Facets(within, "nosuchfield", facets);
        Assert.Empty(facets);
    }

    [Fact]
    public void AFieldIsSpeltTheWayAQueryCanSpellIt()
    {
        MonsterBook book = MonsterBook.Of(Shipped(), null);

        // The column header says "atk spd"; a space separates two terms, so the field cannot.
        Assert.Contains("atkspd", book.Fields);
        Assert.DoesNotContain("atk spd", book.Fields);

        Assert.True(Rows(book, "atkspd>1000") > 0);
        Assert.Equal(Rows(book, "atkspd>1000"), Rows(book, "ATKSPD>1000"));
    }

    private static int Rows(MonsterBook book, string query)
    {
        QueryResult parsed = ColumnQuery.Parse(query);
        Assert.True(parsed.Ok, parsed.Error);

        RowSet rows = Assert.IsType<RowSet>(book.Matching(parsed.Term, out string error));
        Assert.Equal(string.Empty, error);
        return rows.Count;
    }

    /// <summary>The query and the plain walk have to pick the same rows, name for name.</summary>
    private static void Same(
        MonsterVarieties table, MonsterBook book, string query, Func<MonsterVariety, bool> walk)
    {
        QueryResult parsed = ColumnQuery.Parse(query);
        Assert.True(parsed.Ok, parsed.Error);

        RowSet rows = Assert.IsType<RowSet>(book.Matching(parsed.Term, out _));
        var found = new List<int>();
        rows.CopyTo(found);

        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string path, MonsterVariety one) in table.All)
        {
            if (walk(one))
            {
                wanted.Add(path);
            }
        }

        var got = new HashSet<string>(found.Select(row => book.Paths[row]), StringComparer.Ordinal);

        Assert.True(
            wanted.SetEquals(got),
            $"'{query}': the index found {got.Count} and the table says {wanted.Count}"
            + $" - {string.Join(", ", wanted.Except(got).Take(3))} missing,"
            + $" {string.Join(", ", got.Except(wanted).Take(3))} extra");
    }

    private static bool Has(IEnumerable<string> said, string word)
        => said.Any(one => one.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static List<int> Listed(RowSet rows)
    {
        var into = new List<int>();
        rows.CopyTo(into);
        return into;
    }

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
}
