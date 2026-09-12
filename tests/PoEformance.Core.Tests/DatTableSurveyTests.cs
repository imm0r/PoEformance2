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
/// 157 of the 6914 records are .dat files at all, against about 1020 PoE2 tables in the
/// community schemas - fifteen per cent. WorldAreas, MinimapIcons, NPCs and ItemVisualIdentity, the four
/// this project reads rows from, are in none of the 6913 names in any spelling, while Stats,
/// Mods, BaseItemTypes and QuestFlags are.
///
/// THAT SPLIT IS NOT ABOUT WHICH TABLES ARE LOADED. All eight are core tables the client cannot
/// start without - which is why the QuestFlags hunt never once had trouble reading its table. An
/// earlier version of this comment called the four absences a coin flip at 15% coverage; that was
/// worse than wrong, because the four PRESENT ones were picked out of the listing after the fact.
///
/// TWO EXPLANATIONS STOOD, AND ONE IS NOW OUT. The walk might have been seeing a slice of a
/// larger table - but PreloadReader.BucketsBeyondTheCount ran against a live client on
/// 2026-09-01 and found nothing past the last bucket that looks like one, so sixteen is the whole
/// table and coverage is not why anything is missing. What is left is that the table tracks only
/// what the resource loader pulls in, and the open question is why four core tables travel
/// through the loader and four do not.
///
/// THE ASSERTIONS BELOW ARE ABOUT WHAT THE WALK COVERS, which for a while was a real caveat:
/// if BucketCount were too small then every walk of this table would be a fraction of it and
/// these absences would mean only that we did not look. No recording can close that gap - nothing
/// has ever read past the last bucket, in any fixture - so it took the game, and the probe came
/// back clean. What the walk covers IS the table.
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

        // 6913 of 6914, which is what makes the absences below mean anything at all: this is not
        // a capture that only saw part of what it walked.
        Assert.Equal(6913, names.Count);

        // AND THE NUMBER THAT PUTS THOSE ABSENCES IN PROPORTION: 157 .dat records against about
        // 1020 PoE2 tables. Most tables are not here, so no single one being missing is a
        // finding on its own.
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
    public void AMissingTableIsInMemoryAllTheSame()
    {
        // THE ABSENCE, CONFIRMED WITHOUT A NAME. Asking the record names whether MinimapIcons is
        // in the file table can only ever fail one way - a name that did not read looks the same
        // as a name that is not there. Its ROWS answer independently: this capture holds three of
        // them, and no record the walk accepts brackets any of them. So the table is in memory
        // and out of the walk's reach at the same time, which is the fact the two explanations in
        // the type comment have to account for.
        using ReplayMemoryReader replay = Session();
        OffsetSchema schema = RealSessionTests.Schema();

        // Rows of MinimapIcons, found by their Id strings. The addresses are frozen because a
        // recording is a frozen process.
        (ulong At, string Id)[] rows =
        [
            (0x38F4_0294166UL, "Waypoint"),
            (0x38F4_0295842UL, "MapDevice"),
            (0x38F4_0295900UL, "StashPlayer"),
        ];

        long rowSize = schema.Structs["MinimapIconRow"].Constants.TryGetValue("RowSize", out long declared)
            ? declared
            : 0x26;

        foreach ((ulong at, string id) in rows)
        {
            Assert.Equal(id, replay.ReadUnicodeString(replay.ReadPointer(at)));

            // ...and they lie on that table's row grid, which is what says they are rows of ONE
            // table rather than three unrelated strings: 159 and 154 rows apart.
            Assert.Equal(0UL, (at - rows[0].At) % (ulong)rowSize);
        }

        var tables = new LoadedDatTables(replay, schema);
        foreach (LoadedDatTable table in tables.Read(replay.ResolvedStatics["FileRoot"]))
        {
            ulong end = table.Facts.RowsBegin + (ulong)(table.Facts.Rows * table.Facts.RowSize);
            foreach ((ulong at, string id) in rows)
            {
                Assert.False(
                    at >= table.Facts.RowsBegin && at < end,
                    $"{id} at 0x{at:X} is inside {table.Facts.Name} - the walk does reach it after all");
            }
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

        // The three that used to disagree with the computed widths, unchanged between captures
        // and all three explained now. Two were OUR arithmetic - an interval column is two values
        // and costs twice its type, and Mods has eight of them (Stat1Value..Stat8Value, a
        // modifier's min and max roll) for exactly the 32 bytes that were missing. EndgameMaps'
        // single byte is a trailing bool that poe-tool-dev's column list lacks and
        // repoe-fork/dat-export's has. Frozen here because these are the game's numbers, which
        // is what makes them worth checking a column list against.
        Assert.Equal(0x2A5, Assert.Single(tables.FindAll("Mods")).Facts.RowSize);
        Assert.Equal(0xF0, Assert.Single(tables.FindAll("EndgameMaps")).Facts.RowSize);
        Assert.Equal(0x26, Assert.Single(tables.FindAll("AlternateTreeVersions")).Facts.RowSize);
    }
}
