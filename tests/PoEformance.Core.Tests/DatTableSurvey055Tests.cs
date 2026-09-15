using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// The loaded-file table on 0.5.5 (<c>tests/fixtures/session-2026-09-tables-055.rec</c>).
/// </summary>
/// <remarks>
/// THE SAME WALK ON THE CURRENT CLIENT, and it is here because the older capture beside it is
/// pre-0.5.5 and could only ever say what that build did. What it settles is not a new mechanism
/// but the SIZE of an old caveat: FOUR tables were known to be missing from the file table, and on
/// this client it is SIX - every single one this project reaches by dat foreign reference.
///
/// WHY THAT MATTERS RATHER THAN BEING TRIVIA. The file table off FileRoot is the obvious way to
/// reach a dat table by name, and it answers for 153 of them. It answers for NONE of the tables
/// the atlas work depends on: WorldAreas, EndgameMaps, EndgameMapAtlas, EndgameMapContentSet, Tags
/// and PassiveSkills are all absent, while their own neighbours - EndgameMapPins,
/// EndgameMapDecorations, EndgameMapNodeStats - are present. So the foreign-reference route is not
/// a shortcut this project took; it is the only route there is, and this is the measurement that
/// says so on the client the tool actually runs against.
///
/// IT ALSO BOUNDS WHAT THE WALK CAN ANSWER. An address in one of those six, or in the gaps around
/// them, cannot be named by this route however carefully it is asked - which is what a live scan
/// for the string "Traverse" ran into: its row sits between PassiveSkillTreeUIArt and
/// PassiveSkillTreeConnectionArt, in a gap the walk does not cover.
/// </remarks>
public class DatTableSurvey055Tests
{
    /// <summary>What this project reaches through a dat foreign reference rather than by name.</summary>
    private static readonly string[] ReachedByReference =
    [
        "WorldAreas", "EndgameMaps", "EndgameMapAtlas", "EndgameMapContentSet", "Tags", "PassiveSkills",
    ];

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
            return Path.Combine(dir.FullName, "tests", "fixtures", "session-2026-09-tables-055.rec");
        }
    }

    private static LoadedDatTables Walk(ReplayMemoryReader replay)
    {
        var tables = new LoadedDatTables(replay, RealSessionTests.LiveSchema());
        tables.Read(replay.ResolvedStatics["FileRoot"]);
        return tables;
    }

    [Fact]
    public void TheWalkStillFindsMostOfTheClientsTables()
    {
        // 153 of 8165 records, against 134 of 6914 on the pre-0.5.5 capture. The client grew and
        // so did the list; what did not change is that this is the resource loader's inventory
        // rather than "the dat files the game has".
        using ReplayMemoryReader replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        LoadedDatTables tables = Walk(replay);

        Assert.Equal(8165, tables.RecordsWalked);
        Assert.Equal(153, tables.Tables.Count);
        Assert.Empty(tables.LastError);
        Assert.Equal(27281, Assert.Single(tables.FindAll("Stats")).Facts.Rows);
    }

    [Fact]
    public void ANDNotONEOfTheTablesThisProjectReachesByReference()
    {
        // THE POINT OF THE CAPTURE. Four were known absent pre-0.5.5; on this client it is all six,
        // so the foreign-reference route is not a shortcut - it is the only way to any of them.
        using ReplayMemoryReader replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        LoadedDatTables tables = Walk(replay);

        Assert.All(ReachedByReference, name => Assert.Empty(tables.FindAll(name)));

        // And it is not that the walk misses that whole corner of the data: the absent tables'
        // own neighbours come back fine, which is what makes the split a property of the loader
        // rather than of this walk.
        foreach (string neighbour in (string[])["EndgameMapPins", "EndgameMapDecorations", "EndgameMapNodeStats"])
        {
            Assert.NotEmpty(tables.FindAll(neighbour));
        }
    }

    [Fact]
    public void SoAnAddressInsideOneOfThemCannotBeNamedByThisRoute()
    {
        // The limit, stated as the case that found it. A live scan for the UTF-16 string
        // "Traverse" landed on a dat row at 0x3DDD9EC44C8; the walk cannot say which table that
        // is, because the row falls in a gap between two tables it DOES see. Nothing is wrong
        // with the walk - the answer is simply not in it, and a test saying so is cheaper than
        // running the same capture again in three months.
        const ulong Row = 0x3DDD9EC44C8;

        using ReplayMemoryReader replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        LoadedDatTables tables = Walk(replay);

        Assert.DoesNotContain(
            tables.Tables,
            table => Row >= table.Facts.RowsBegin
                && Row < table.Facts.RowsBegin + (ulong)(table.Facts.Rows * table.Facts.RowSize));
    }
}
