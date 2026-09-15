using System.Text;
using PoEformance.Game.Components;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// The game's own <c>.csd</c> stat descriptions, read out of the install.
/// </summary>
/// <remarks>
/// WHAT THESE CAN AND CANNOT SETTLE, said plainly because the difference matters. Nothing here has
/// a PoE2 install to parse - a recording holds memory reads, not files - so no test in this project
/// can put the reader in front of the real thing. What they do is pin the FORMAT, against text
/// written from tools/poe_tools.py's own parser, which this project ships and which produced
/// data/stat_desc_map.tsv. Where this reader and that one disagree about a line's shape, one of
/// these fails.
///
/// THE CHECK AGAINST THE GAME IS ON THE MACHINE THAT HAS IT. AtlasWatch.Check prints how many of
/// the install's sentences agree with the shipped export, and spells out the ones that differ -
/// two completely separate routes to the same text, which is the strongest available evidence that
/// either is right. See StatDescriptions.Against.
///
/// THE THREE TRAPS BELOW ARE NOT HYPOTHETICAL. Each is a line inside a block that is not a wording,
/// and each was worth a wrong entry when this reader was first written: an include line is a quoted
/// PATH and would have been filed as the stat's sentence; no_description ends a block that has
/// none; and a lang line that is not English does not end the block, because a file may list
/// another language before English rather than after it.
/// </remarks>
public class StatDescriptionFilesTests
{
    /// <summary>A file in the shape the game writes them, without a lang line - so English throughout.</summary>
    private const string Plain = """
        description
        	1 map_num_extra_shrines
        		1 1 "Area contains an additional Shrine"
        		1 # "Area contains {0} additional Shrines"
        description
        	1 map_item_drop_rarity_+%
        		1 # "{0}% increased Rarity of Items found in this Area"
        """;

    [Fact]
    public void ABLOCKIsItsStatsAndTheFirstWordingUnderThem()
    {
        List<StatDescription> blocks = StatDescriptionFiles.Parse(Plain);

        Assert.Equal(2, blocks.Count);
        Assert.Equal(["map_num_extra_shrines"], blocks[0].Stats);

        // THE FIRST, not the last. A block lists one line per value range - "an additional Shrine"
        // against "{0} additional Shrines" - and they are the same sentence in different grammar,
        // so which is taken changes wording without changing meaning. The reference takes the
        // first, and a reader that took the last would quietly say something else.
        Assert.Equal("Area contains an additional Shrine", blocks[0].Template);
        Assert.Equal("{0}% increased Rarity of Items found in this Area", blocks[1].Template);
    }

    [Fact]
    public void ANINCLUDEIsAPathAndNotASentence()
    {
        // The trap: it is a QUOTED line inside a block, so a reader taking the first quoted line
        // files the path of another file as this stat's sentence - and the atlas then shows
        // "Metadata/StatDescriptions/stat_descriptions.csd" where a sentence should be.
        List<StatDescription> blocks = StatDescriptionFiles.Parse("""
            include "Metadata/StatDescriptions/stat_descriptions.csd"
            description
            	1 map_num_extra_shrines
            		include "Metadata/StatDescriptions/specific.csd"
            		1 1 "Area contains an additional Shrine"
            """);

        Assert.Equal("Area contains an additional Shrine", Assert.Single(blocks).Template);
    }

    [Fact]
    public void ABLOCKThatSaysItHasNoDescriptionGetsNone()
    {
        // And crucially does not borrow the next block's - which is what happens when the marker
        // is not understood and the scan simply carries on looking for a quoted line.
        List<StatDescription> blocks = StatDescriptionFiles.Parse("""
            description
            	1 some_hidden_stat
            		no_description
            description
            	1 map_num_extra_shrines
            		1 1 "Area contains an additional Shrine"
            """);

        Assert.Equal("map_num_extra_shrines", Assert.Single(blocks).Stats[0]);
    }

    [Fact]
    public void ANDEnglishIsTakenWhereverInTheBlockItIs()
    {
        // A lang line switches language; it does not end the block. Giving up at the first foreign
        // one costs every stat in a file that happens to list another language first.
        List<StatDescription> blocks = StatDescriptionFiles.Parse("""
            description
            	1 map_num_extra_shrines
            		lang "German"
            		1 1 "Das Gebiet enthält einen zusätzlichen Schrein"
            		lang "English"
            		1 1 "Area contains an additional Shrine"
            		lang "French"
            		1 1 "pas celui-ci"
            """);

        Assert.Equal("Area contains an additional Shrine", Assert.Single(blocks).Template);
    }

    [Fact]
    public void ANDAFileWithNoLangLineAtAllIsEnglish()
    {
        // Which is most of them. "No language said" cannot mean "no English here", or the general
        // file - the one that decides nearly every wording - would read as empty.
        Assert.Equal(2, StatDescriptionFiles.Parse(Plain).Count);
    }

    [Fact]
    public void THECountOnTheStatLineSaysHowManyStatsTheBlockCovers()
    {
        List<StatDescription> blocks = StatDescriptionFiles.Parse("""
            description
            	3 active_skill_all_damage_%_as_fire_if_heat_is_consumed heat_consumption_amount heat_extra
            		1 # "Damage Gained as Fire on {0} Heat Consumption@{1}%"
            description
            	1 map_num_extra_shrines
            		1 1 "Area contains an additional Shrine"
            """);

        Assert.Equal(2, blocks.Count);
        Assert.Equal(3, blocks[0].Stats.Count);
        Assert.Equal("heat_consumption_amount", blocks[0].Stats[1]);
    }

    [Fact]
    public void THEGAMESMarkupIsStrippedToWhatItDisplays()
    {
        // The same job KeywordGlossary.Plain does for strings out of memory: the game writes a
        // keyword as [Code|Display], or as [Text] where the two are the same.
        Assert.Equal("Area contains an Otherworldly Breach", StatDescriptionFiles.Plain(
            "Area contains an Otherworldly [Breach|Breach]"));
        Assert.Equal("Area contains a Shrine", StatDescriptionFiles.Plain("Area contains a [Shrine]"));
        Assert.Equal("nothing to strip", StatDescriptionFiles.Plain("nothing to strip"));

        // An unbalanced bracket is kept verbatim rather than swallowing the rest of the sentence.
        Assert.Equal("half [open", StatDescriptionFiles.Plain("half [open"));
    }

    [Fact]
    public void ANDItIsStrippedOnTheWayOutOfTheParserToo()
    {
        Assert.Equal(
            "Area contains an Otherworldly Breach",
            Assert.Single(StatDescriptionFiles.Parse("""
                description
                	1 map_num_breaches
                		1 1 "Area contains an Otherworldly [Breach|Breach]"
                """)).Template);
    }

    [Theory]
    [InlineData("utf-16-with-mark")]
    [InlineData("utf-16-without-mark")]
    [InlineData("utf-8-with-mark")]
    [InlineData("utf-8")]
    public void AFILEReadsWhicheverWayItIsEncoded(string how)
    {
        // The reference decodes every one of them as UTF-16LE - its utf-8 fallback sits behind an
        // except a replacing decode never reaches - so a file without a mark still reads there.
        // Copying that by accident would turn a UTF-8 file into mojibake, and testing the shape
        // rather than assuming one encoding is what makes both work.
        byte[] content = how switch
        {
            "utf-16-with-mark" => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(Plain)],
            "utf-16-without-mark" => Encoding.Unicode.GetBytes(Plain),
            "utf-8-with-mark" => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(Plain)],
            _ => Encoding.UTF8.GetBytes(Plain),
        };

        Assert.Equal(2, StatDescriptionFiles.Parse(StatDescriptionFiles.Decode(content)).Count);
    }

    [Fact]
    public void SOMETHINGThatIsNotAFileIsNoBlocksRatherThanAThrow()
    {
        Assert.Empty(StatDescriptionFiles.Parse(null));
        Assert.Empty(StatDescriptionFiles.Parse(string.Empty));
        Assert.Empty(StatDescriptionFiles.Parse("not a stat description file at all"));

        // A block cut off before its wording, which is what a truncated read leaves.
        Assert.Empty(StatDescriptionFiles.Parse("description\n\t1 map_num_extra_shrines\n"));

        // And a stat line that is not one: the count has to be a count.
        Assert.Empty(StatDescriptionFiles.Parse("""
            description
            	not_a_count map_num_extra_shrines
            		1 1 "Area contains an additional Shrine"
            """));
    }

    [Fact]
    public void WITHOUTAnInstallThereIsNothingToRead()
    {
        // The ordinary case on a machine that has no game on it, and on every machine running
        // these tests. It is not an error: the tokens stay numbers, which is where they started.
        Assert.Empty(StatDescriptionFiles.Read(null));

        StatDescriptions lines = StatDescriptions.FromInstall(null);
        Assert.Equal(0, lines.Count);
        Assert.False(lines.FromGame);
    }

    /// <summary>An install in memory, which is as close to the real thing as this can get.</summary>
    private sealed class Fake : IGameArchive
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

        public bool Ready => true;

        public string Describe => "a made-up install";

        public void Add(string path, byte[] bytes) => _files[path] = bytes;

        public byte[]? Read(string path) => _files.GetValueOrDefault(path);

        public byte[]? Read(string path, int at, int length)
            => !_files.TryGetValue(path, out byte[]? bytes) || at < 0 || length < 0
               || at + (long)length > bytes.Length
                ? null
                : bytes[at..(at + length)];
    }

    /// <summary>The general file, which is the one whose wordings win.</summary>
    private const string GeneralCsd = """
        description
        	1 map_num_extra_shrines
        		1 1 "Area contains an additional Shrine"
        description
        	1 base_maximum_life
        		1 # "{0} to maximum Life"
        description
        	2 heat_consumption_amount heat_extra
        		1 # "Damage Gained as Fire on {0} Heat Consumption@{1}%"
        """;

    /// <summary>A specific file, which says something else about a stat the general one covers.</summary>
    private const string SkillsCsd = """
        description
        	1 map_num_extra_shrines
        		1 1 "this skill's own wording, which must not win"
        description
        	1 skill_only_stat
        		1 # "Skill does {0} things"
        """;

    private static byte[] Utf16(string text)
        => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)];

    /// <summary>An install holding two .csd files, spelled-out paths and all.</summary>
    private static GameFiles Install()
    {
        byte[] general = Utf16(GeneralCsd);
        byte[] skills = Utf16(SkillsCsd);

        var content = new byte[8192];
        general.CopyTo(content, 0);
        skills.CopyTo(content, 4096);

        var archive = new Fake();
        archive.Add("data.bundle.bin", Packed.Bundle(content, chunkSize: 512));
        archive.Add("_.index.bin", Packed.Bundle(
            Packed.Index(
                ["data"],
                [
                    new("data/statdescriptions/stat_descriptions.csd", 0, 0, general.Length),
                    new("data/statdescriptions/skills/skill_stat_descriptions.csd", 0, 4096, skills.Length),
                ],
                paths: Packed.Paths(
                    ["data/statdescriptions/"],
                    [(0, "stat_descriptions.csd"), (0, "skills/skill_stat_descriptions.csd")])),
            chunkSize: 512));

        GameFiles? files = GameFiles.Open(archive, Packed.AsIs);
        Assert.NotNull(files);
        return files!;
    }

    [Fact]
    public void ANINSTALLSaysWhatItsStatsMeanWithoutAnyExportInBetween()
    {
        // END TO END, which is the whole point of the exercise: an archive holding bundles, an
        // index saying what is in which, the spelled-out paths naming the .csd files, and the
        // sentences coming back out of them. No file in data/ is touched anywhere in this.
        GameFiles files = Install();

        BundleIndex.WalkedNames walked = files.Names(
            null, StatDescriptionFiles.Folder, StatDescriptionFiles.Extension);
        Assert.Equal(2, walked.Inside.Count);

        StatDescriptions lines = StatDescriptions.FromInstall(files, walked.Inside);

        Assert.True(lines.FromGame);
        Assert.Contains(".csd", lines.Source, StringComparison.Ordinal);
        Assert.Equal("Area contains an additional Shrine", lines.Of("map_num_extra_shrines"));
        Assert.Equal("Skill does {0} things", lines.Of("skill_only_stat"));
    }

    [Fact]
    public void ANDTheGeneralFileDecidesTheWordingRatherThanWhicheverCameSecond()
    {
        // FIRST FOUND WINS, so which file is read first IS the answer for every stat that appears
        // in more than one - and a skill file's own phrasing of a general stat is not the one the
        // atlas wants. Walk order is the install's business, so the read sorts rather than trusts
        // it: the fixture above lists the general file second on purpose.
        GameFiles files = Install();
        List<string> walked = files.Under(StatDescriptionFiles.Folder, StatDescriptionFiles.Extension);

        Assert.Equal(
            "Area contains an additional Shrine",
            StatDescriptions.FromInstall(files, Enumerable.Reverse(walked)).Of("map_num_extra_shrines"));
    }

    [Fact]
    public void ANDAStatWrittenAsBaseSomethingAnswersToBothNames()
    {
        // The game writes some stats as base_X and refers to them as X. A lookup that misses one
        // of the two is a sentence lost for no reason, so the short form is added as an alias.
        GameFiles files = Install();
        StatDescriptions lines = StatDescriptions.FromInstall(files);

        Assert.Equal("{0} to maximum Life", lines.Of("base_maximum_life"));
        Assert.Equal("{0} to maximum Life", lines.Of("maximum_life"));
    }

    [Fact]
    public void AMULTIStatBlockIsDroppedRatherThanHalfFilled()
    {
        // The same rule the shipped table keeps, applied where the blocks still SAY which stats
        // share a sentence. A caller holds ONE stat's value and cannot fill the other holes, so
        // rendering such a line would print a sentence about numbers nobody has.
        StatDescriptions lines = StatDescriptions.FromInstall(Install());

        Assert.Null(lines.Of("heat_consumption_amount"));
        Assert.Null(lines.Of("heat_extra"));
        Assert.Equal(2, lines.Grouped);
    }

    [Fact]
    public void ANDTheTwoRoutesToTheSameSentenceCanBeCompared()
    {
        // THE ONLY CHECK AVAILABLE ON A MACHINE WITH THE GAME, and the reason Against exists: this
        // reader over the install against data/stat_desc_map.tsv, which a Python tool built from
        // files a third tool unpacked. Agreement is two independent paths meeting; disagreement is
        // printed rather than counted, because a tally cannot say which of them is wrong.
        StatDescriptions lines = StatDescriptions.FromInstall(Install());
        IReadOnlyList<string> against = lines.Against(lines);

        Assert.Contains($"{lines.Count} of {lines.Count} agree", Assert.Single(against), StringComparison.Ordinal);

        // Nothing to compare against is said rather than counted as agreement.
        Assert.Contains("nothing to compare", Assert.Single(lines.Against(StatDescriptions.Empty)), StringComparison.Ordinal);
    }
}
