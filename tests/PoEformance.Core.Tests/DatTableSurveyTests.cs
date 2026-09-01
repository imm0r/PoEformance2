using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Files;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// What the loaded-file table actually contains, against a walk that named all of it
/// (<c>tests/fixtures/session-2026-09-tables.rec</c>, 638 KB).
/// </summary>
/// <remarks>
/// THE CAPTURE THAT SETTLES THE ROUTE'S LIMIT. Every other recording here reads a file record's
/// NAME only for the handful that pass some filter - the newest area stamp, or the dat-table
/// checks - so none of them could ever answer "is table X in this table at all". `--tables`
/// reads the name of every record that is not a table, and this session holds 6913 of 6914.
///
/// The answer is a limit: WorldAreas, MinimapIcons, NPCs and ItemVisualIdentity - the four
/// tables this project already reads rows from - are in none of the 6913 names, in any spelling,
/// while Stats, Mods, BaseItemTypes and QuestFlags all are. The client plainly has those four
/// tables' rows, so it reaches them some other way, and the row-pointer route through a
/// component stays the only known way to them.
///
/// SAID PRECISELY, THAT IS "NOT IN THE SIXTEEN BUCKETS WE WALK". BucketCount is 0x10 because
/// GameHelper2 says so, and GameHelper2 is a PoE1 tool; nothing here has checked it against this
/// game. If the real count is larger then every walk of this table is a fraction of it and these
/// absences mean only that we did not look - so the assertions below are about what the walk
/// covers, which is the thing they can be about. No recording can close that gap: nothing has
/// ever read past the last bucket, in any fixture. PreloadReader.BucketsBeyondTheCount is the
/// read that asks, and it needs the game.
///
/// So the file table is not "the dat files the game has". It is the resource system's list (the
/// schema's hover note puts FileRoot 0xFA08 from the object that turned out to be exactly that),
/// and it is mostly art: 157 of its 6914 records are .dat files at all, against 1219 .tok, 903
/// .ao and 462 .ast.
/// </remarks>
public class DatTableSurveyTests
{
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
            return Path.Combine(dir.FullName, "tests", "fixtures", "session-2026-09-tables.rec");
        }
    }

    private static ReplayMemoryReader Session() => ReplayMemoryReader.Load(File.OpenRead(FixturePath));

    [Fact]
    public void BeingInTheFileTableAndBeingParsedAreDifferentThings()
    {
        // Built on the guess that the two could come apart, and here they do: 23 records call
        // themselves .dat files and have nothing usable at RowStorePtr. GrantedEffectsPerLevel
        // and Languages are among them - files the client certainly uses - so this is not a
        // broken record, it is a table that has not been parsed into rows at this moment.
        using ReplayMemoryReader replay = Session();
        OffsetSchema schema = RealSessionTests.Schema();

        var tables = new LoadedDatTables(replay, schema);
        DatTableSurvey survey = tables.Survey(replay.ResolvedStatics["FileRoot"]);

        Assert.Equal(6914, survey.Records);
        Assert.Equal(134, survey.Tables.Count);
        Assert.Equal(23, survey.Refused.Count);
        Assert.Contains("Data/Balance/GrantedEffectsPerLevel.dat", survey.Refused);
        Assert.Contains("Data/Balance/Languages.dat", survey.Refused);
    }

    [Fact]
    public void TheFourTablesThisProjectReadsRowsFromAreInNoneOfTheNames()
    {
        // THE ANSWER TO THE OPEN QUESTION, within what the walk covers. Asked of the survey they
        // are missing from both lists, which on its own could still mean their records are there
        // under an unreadable name - so this asks by name, over every record the walk reaches.
        // What it cannot ask about is a bucket past the sixteenth; see the type comment.
        using ReplayMemoryReader replay = Session();
        OffsetSchema schema = RealSessionTests.Schema();

        var files = new PreloadReader(replay, schema);
        var names = new List<string>();
        foreach (ulong record in files.Records(replay.ResolvedStatics["FileRoot"]))
        {
            string name = replay.ReadStdWString(
                record + (ulong)schema.Structs["FileRecord"].OffsetOf("Name"), PreloadReader.LongestPath);
            if (name.Length > 0)
            {
                names.Add(name);
            }
        }

        // 6913 of 6914, which is what makes the absences below mean something: this is not a
        // capture that only saw part of the table.
        Assert.Equal(6913, names.Count);
        Assert.Equal(157, names.Count(n => n.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)));

        foreach (string table in (string[])["WorldAreas", "MinimapIcons", "NPCs", "ItemVisualIdentity"])
        {
            Assert.DoesNotContain(names, n => n.EndsWith($"/{table}.dat", StringComparison.OrdinalIgnoreCase));
        }

        // And the contrast that makes it a fact about those four rather than about dat files:
        // the big content tables ARE here.
        foreach (string table in (string[])["Stats", "Mods", "BaseItemTypes", "QuestFlags"])
        {
            Assert.Contains(names, n => n.EndsWith($"/{table}.dat", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void AndASecondSessionReportsTheSameRowSizes()
    {
        // The row sizes are the game's own arithmetic, so they should not move between two
        // captures of the same build - and they do not. Worth asserting because the whole
        // 123-table confirmation of the column widths rests on these numbers being a property
        // of the game rather than of one moment in it.
        using ReplayMemoryReader replay = Session();
        OffsetSchema schema = RealSessionTests.Schema();

        var tables = new LoadedDatTables(replay, schema);
        tables.Read(replay.ResolvedStatics["FileRoot"]);

        (string Table, string Struct)[] declared =
        [
            ("QuestFlags", "QuestFlagsRow"),
            ("BuffDefinitions", "BuffDefinition"),
            ("KeywordPopups", "KeywordPopupsRow"),
        ];

        foreach ((string table, string row) in declared)
        {
            LoadedDatTable loaded = Assert.Single(tables.FindAll(table));
            Assert.Equal(schema.Structs[row].Constants["RowSize"], loaded.Facts.RowSize);
        }

        // The three that used to disagree with the computed widths, unchanged between captures.
        // Two of them were OUR arithmetic - an interval column is two values and costs twice its
        // type, and Mods has eight of them (Stat1Value..Stat8Value, a modifier's min and max
        // roll) for exactly the 32 bytes that were missing. EndgameMaps' one byte is still
        // unexplained. Frozen here because these are the game's numbers either way.
        Assert.Equal(0x2A5, Assert.Single(tables.FindAll("Mods")).Facts.RowSize);
        Assert.Equal(0xF0, Assert.Single(tables.FindAll("EndgameMaps")).Facts.RowSize);
        Assert.Equal(0x26, Assert.Single(tables.FindAll("AlternateTreeVersions")).Facts.RowSize);
    }
}
