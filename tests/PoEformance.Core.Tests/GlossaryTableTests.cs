using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// The whole route from a static to the glossary, against the session that first ran it
/// (<c>tests/fixtures/session-2026-08-glossary.rec</c>, 497 KB).
/// </summary>
/// <remarks>
/// THE HOP THAT HAD NO FIXTURE. Every other recording in this repo walks the loaded-file table
/// for its NAMES: the preload reader takes a 32-byte string header at record+0x08 and stops one
/// byte short of RowStorePtr at +0x28, so no capture had ever read a file record's row store,
/// and the claim that a dat table IS a file record could be checked everywhere except at the
/// place it pays off. This capture is `--glossary --record` on a live client, and it goes all
/// the way through.
///
/// What makes it worth keeping as a test rather than as a screenshot is that the numbers are
/// the GAME'S. 0x48 identified this table from a dissector window by column arithmetic over
/// dat-schema; here the client says 0x48 itself, because a row store divides its rows by its
/// by-Id index and needs no schema to do it. If a patch adds a column, this test fails on the
/// game's own arithmetic rather than on ours.
/// </remarks>
public class GlossaryTableTests
{
    /// <summary>The KeywordPopups table in that session, as the file table holds it.</summary>
    private const ulong Table = 0x38F6_074E6D0UL;

    /// <summary>Its row store, which is the field no other fixture reaches.</summary>
    private const ulong RowStore = 0x38F7_7FE21F0UL;

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
            return Path.Combine(dir.FullName, "tests", "fixtures", "session-2026-08-glossary.rec");
        }
    }

    private static ReplayMemoryReader Session() => ReplayMemoryReader.Load(File.OpenRead(FixturePath));

    [Fact]
    public void TheRouteRunsFromTheStaticToTheRows()
    {
        using ReplayMemoryReader replay = Session();
        OffsetSchema schema = RealSessionTests.Schema();

        var tables = new LoadedDatTables(replay, schema);
        IReadOnlyList<LoadedDatTable> loaded = tables.Read(replay.ResolvedStatics["FileRoot"]);

        Assert.Equal(string.Empty, tables.LastError);
        Assert.Equal(6907, tables.RecordsWalked);
        Assert.Equal(131, loaded.Count);

        LoadedDatTable found = Assert.Single(tables.FindAll(KeywordGlossary.TableName));
        Assert.Equal(Table, found.Address);
        Assert.Equal("Data/Balance/KeywordPopups.dat", found.Facts.Path);

        // The last hop: no other fixture holds these bytes.
        Assert.Equal(
            RowStore,
            replay.ReadPointer(Table + (ulong)schema.Structs["DatTable"].OffsetOf("RowStorePtr")));

        // THE GAME'S OWN ARITHMETIC, not the schema's - rows divided by by-Id entries.
        Assert.Equal(1026, found.Facts.Rows);
        Assert.Equal(schema.Structs["KeywordPopupsRow"].Constants["RowSize"], found.Facts.RowSize);
    }

    [Fact]
    public void AndTheRowsReadAsTheGlossary()
    {
        using ReplayMemoryReader replay = Session();
        OffsetSchema schema = RealSessionTests.Schema();

        var tables = new LoadedDatTables(replay, schema);
        tables.Read(replay.ResolvedStatics["FileRoot"]);
        KeywordGlossary glossary = KeywordGlossary.Read(tables, replay, schema);

        Assert.Equal(string.Empty, glossary.LastError);
        Assert.Equal(1026, glossary.ById.Count);

        // Every row has an Id, and every Id is distinct - which is what says the stride is right
        // over the whole table rather than at the two rows somebody looked at.
        Assert.Equal("Physical Damage", glossary.Lookup("Physical")?.Term);
        Assert.Equal("Critical Hits", glossary.Lookup("Critical")?.Term);
        Assert.Equal("Power Rune", glossary.Lookup("PowerRune")?.Term);

        // The game's own placeholder row, typo and all. Row 0 of the table, so it is also the
        // proof that the walk starts where the rows do.
        Assert.Equal(
            "This test case is designed to be overwitten by other content",
            glossary.Lookup("Test")?.Definition);
    }

    [Fact]
    public void TheMarkupNamesRowsOfTheSameTable()
    {
        // What the table is FOR, checked over all of it rather than over an example: a
        // definition refers to other keywords, and those keywords are rows here. 313 of the 317
        // distinct keys resolve; the four that do not are the game's own placeholders (DNT,
        // DNT-UNUSED) and two leftovers, which is data rather than a failed read.
        using ReplayMemoryReader replay = Session();
        OffsetSchema schema = RealSessionTests.Schema();

        var tables = new LoadedDatTables(replay, schema);
        tables.Read(replay.ResolvedStatics["FileRoot"]);
        KeywordGlossary glossary = KeywordGlossary.Read(tables, replay, schema);

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (KeywordPopup entry in glossary.ById.Values)
        {
            foreach (string key in KeywordGlossary.KeysIn(entry.Definition))
            {
                keys.Add(key);
            }
        }

        Assert.Equal(317, keys.Count);
        Assert.Equal(4, keys.Count(key => glossary.Lookup(key) is null));

        Assert.StartsWith(
            "Physical damage is one of the five Damage Types.",
            KeywordGlossary.Plain(glossary.Lookup("Physical")?.Definition),
            StringComparison.Ordinal);
    }

    [Fact]
    public void NoDefinitionComesBackAtTheCap()
    {
        // THE CHECK THAT WOULD HAVE CAUGHT THE BUG THIS FIXTURE FOUND. A raw UTF-16 pointer
        // carries no length, so a definition longer than the reader's cap is truncated rather
        // than refused - silently, and only in the wordiest rows. The first version of this
        // reader capped at 512 and cut 56 of these definitions mid-word.
        //
        // A string that comes back at EXACTLY the cap is what that looks like from the inside,
        // so the test is that none does. It cannot prove no text is longer than the cap in some
        // future patch, which is the point: it fails when one is.
        using ReplayMemoryReader replay = Session();
        OffsetSchema schema = RealSessionTests.Schema();

        var tables = new LoadedDatTables(replay, schema);
        tables.Read(replay.ResolvedStatics["FileRoot"]);
        KeywordGlossary glossary = KeywordGlossary.Read(tables, replay, schema);

        int longest = glossary.ById.Values.Max(e => e.Definition.Length);
        Assert.InRange(longest, 512, 2047);
    }

    [Fact]
    public void ThePercentSignsAreNotDoubled()
    {
        // The correction this fixture forced. The dissector showed "+100%%" and this code
        // collapsed the doubling; the raw column holds "+100%", and the doubling was ImGui
        // escaping in our own text path (ImGuiText.Escape). Frozen here so it cannot come back:
        // the table has 236 definitions with a percent in them and none with two.
        using ReplayMemoryReader replay = Session();
        OffsetSchema schema = RealSessionTests.Schema();

        var tables = new LoadedDatTables(replay, schema);
        tables.Read(replay.ResolvedStatics["FileRoot"]);
        KeywordGlossary glossary = KeywordGlossary.Read(tables, replay, schema);

        Assert.Equal(
            236,
            glossary.ById.Values.Count(e => e.Definition.Contains('%', StringComparison.Ordinal)));
        Assert.DoesNotContain(
            glossary.ById.Values,
            e => e.Definition.Contains("%%", StringComparison.Ordinal));

        Assert.Contains(
            "+100% (i.e. twice as much damage)",
            KeywordGlossary.Plain(glossary.Lookup("CriticalDamageBonus")?.Definition),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheRichTextMarkupIsLeftAlone()
    {
        // The 33 Expedition rune rows are written in a different markup - colours, a font, an
        // italic, and a style reference - and braces appear in those rows and nowhere else, so
        // the two markups do not overlap. Plain() passes it through rather than half-rendering
        // it, and this is where that decision is written down rather than assumed.
        using ReplayMemoryReader replay = Session();
        OffsetSchema schema = RealSessionTests.Schema();

        var tables = new LoadedDatTables(replay, schema);
        tables.Read(replay.ResolvedStatics["FileRoot"]);
        KeywordGlossary glossary = KeywordGlossary.Read(tables, replay, schema);

        int braced = glossary.ById.Values.Count(e => e.Definition.Contains('{', StringComparison.Ordinal));
        Assert.Equal(33, braced);
        Assert.Equal(
            braced,
            glossary.ById.Values.Count(e => e.Definition.Contains("<rgb(", StringComparison.Ordinal)));

        string rune = glossary.Lookup("FireRune")!.Value.Definition;
        Assert.StartsWith("<<ExpedRuneFire>><rgb(219,217,206)>{Fire Rune}", rune, StringComparison.Ordinal);
        Assert.Equal(rune, KeywordGlossary.Plain(rune));   // no brackets, so not even copied
    }
}
