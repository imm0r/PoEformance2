using System.Text;
using PoEformance.Game.Diagnostics;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// The .ao reader, against text rather than against an install.
/// </summary>
/// <remarks>
/// NO INSTALL AND NO FIXTURE, which is the constraint this was written under and is worth being
/// honest about: nothing in this repository is a real .ao file, so these tests settle whether the
/// reader implements the FORMAT as the two references describe it, not whether the game's own
/// files are shaped that way. What settles that is one run of the diagnostic on a machine with the
/// game, which is why the reader records faults instead of throwing - see
/// <see cref="NothingIsDroppedQuietly"/>. The references are the format diagram and zao's
/// libpoe/poe/format/ao.cpp.
///
/// THE ADVERSARIAL HALF IS THE IMPORTANT HALF. A reader that quietly swallows what surprises it
/// would make the measurement it exists for come back tidy and wrong, and the ways a hand-written
/// scanner fails are all silent: a loop that does not advance, a struct that eats the one after
/// it, a complaint counted three times. Each of those is a test below because each of them
/// actually happened while this was being written.
/// </remarks>
public class AnimatedObjectTests
{
    /// <summary>A file exercising every shape the two references between them describe.</summary>
    private const string Sample = """
        version 3
        abstract
        extends "Metadata/Monsters/Monster.ao"
        extends "nothing"

        /* a multi-line
           comment */
        // and a single one

        AnimationController
        {
        	metdata = "Metadata/Monsters/Skeleton/Skeleton.amd"
        	on_event = { PlayEffect("Metadata/Effects/foo.ao", 1); if (x) { Add("a/b.epk"); } }
        	animation = "stance2"
        		0.35 = "sound/hit.ogg"
        		1,2 = "event"
        	blend = 0.75
        }

        AOSet {
        	ao = "Metadata/Monsters/Skeleton/Weapon.ao"
        	fixed_ao = "Metadata/Monsters/Skeleton/Shield.ao"
        }

        AttachedAnimatedObject {
        	attached_object = "Metadata/Effects/glow.ao"
        	attached_object = "Metadata/Effects/glow2.ao"
        }

        EffectDrivenEvent {
        	monster = '{ "a": "has a \" quote",
        	             "b": 2 }'
        }

        Brackets {}

        client {
        	Functions {
        		enabled = true
        	}
        }
        """;

    [Fact]
    public void TheHeaderReadsAsTheReferenceSpellsIt()
    {
        AnimatedObject ao = AnimatedObject.Parse(Sample);

        Assert.Empty(ao.Faults);
        Assert.Equal(3, ao.Version);
        Assert.True(ao.Abstract);
        Assert.True(ao.Ready);

        // "extends .ao file or nothing" is the diagram's own wording, and "nothing" is a literal
        // that appears in the quotes rather than an absent line. Kept as written.
        Assert.Equal(["Metadata/Monsters/Monster.ao", "nothing"], ao.Extends);
    }

    /// <summary>
    /// A key may repeat, so the entries are a list and not a dictionary.
    /// </summary>
    /// <remarks>
    /// THE DIAGRAM SAYS SO IN AS MANY WORDS - "Key - NOT unique" - and the cost of getting it
    /// wrong is invisible: a monster with three attachments would show one, and the file would
    /// look like it only ever had one.
    /// </remarks>
    [Fact]
    public void ARepeatedKeyIsKeptTwice()
    {
        AnimatedObject ao = AnimatedObject.Parse(Sample);

        AoStruct attached = Assert.Single(ao.Named("AttachedAnimatedObject"));
        Assert.Equal(2, attached.Entries.Count);
        Assert.All(attached.Entries, one => Assert.Equal("attached_object", one.Key));
        Assert.Equal(
            ["Metadata/Effects/glow.ao", "Metadata/Effects/glow2.ao"],
            attached.Entries.Select(one => one.Value));
    }

    /// <summary>
    /// The four value spellings, each read as its own kind.
    /// </summary>
    /// <remarks>
    /// THE SCRIPT IS THE ONE A LINE-BASED READER LOSES. It nests braces AND carries a quoted file
    /// path, so a reader counting braces without honouring quotes closes the block early and turns
    /// the rest of the file into rubbish. The payload is the other one: it spans lines and holds a
    /// double quote, which is exactly what a naive "value is the rest of the line" rule breaks on.
    /// </remarks>
    [Fact]
    public void EachOfTheFourValueSpellingsIsReadAsItself()
    {
        AnimatedObject ao = AnimatedObject.Parse(Sample);
        AoStruct controller = Assert.Single(ao.Named("AnimationController"));

        AoEntry meta = controller.Entries[0];
        Assert.Equal("metdata", meta.Key);
        Assert.Equal(AoValueKind.Quoted, meta.Kind);
        Assert.Equal("Metadata/Monsters/Skeleton/Skeleton.amd", meta.Value);

        AoEntry script = controller.Entries[1];
        Assert.Equal(AoValueKind.Script, script.Kind);
        Assert.StartsWith("{", script.Value, StringComparison.Ordinal);
        Assert.EndsWith("}", script.Value, StringComparison.Ordinal);

        // Whole, including the inner braces and the path inside the quotes.
        Assert.Contains("Add(\"a/b.epk\");", script.Value, StringComparison.Ordinal);
        Assert.Contains("Metadata/Effects/foo.ao", script.Value, StringComparison.Ordinal);

        AoEntry number = controller.Entries[3];
        Assert.Equal("blend", number.Key);
        Assert.Equal(AoValueKind.Native, number.Kind);
        Assert.Equal("0.75", number.Value);

        AoEntry payload = Assert.Single(Assert.Single(ao.Named("EffectDrivenEvent")).Entries);
        Assert.Equal(AoValueKind.Payload, payload.Kind);
        Assert.Contains("\\\" quote", payload.Value, StringComparison.Ordinal);
        Assert.Contains("\"b\": 2", payload.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Indentation makes children, and an animation's children are keyed by TIME.
    /// </summary>
    /// <remarks>
    /// The reference's own abandoned patterns are the evidence for this shape: a top-level entry
    /// matches \t(\S+) = (.*), a child \t\t(\S+) = (.*), and a keyframe \t\t([0-9.,]+) = (.*).
    /// So "0.35" and "1,2" are keys, which is why the key rule cannot be restricted to a name.
    /// </remarks>
    [Fact]
    public void IndentationMakesChildrenAndAKeyframeKeyIsATime()
    {
        AnimatedObject ao = AnimatedObject.Parse(Sample);
        AoStruct controller = Assert.Single(ao.Named("AnimationController"));

        AoEntry animation = controller.Entries[2];
        Assert.Equal("animation", animation.Key);
        Assert.Equal("stance2", animation.Value);
        Assert.Equal(["0.35", "1,2"], animation.Children.Select(one => one.Key));

        // And the entry after them is a sibling of the animation, not another of its children -
        // the indentation drops back, which is the whole rule.
        Assert.Equal("blend", controller.Entries[3].Key);
        Assert.Empty(controller.Entries[3].Children);
    }

    [Fact]
    public void AClientBlockHoldsStructsAndTheyAreMarked()
    {
        AnimatedObject ao = AnimatedObject.Parse(Sample);

        AoStruct functions = Assert.Single(ao.Named("Functions"));
        Assert.True(functions.Client);
        Assert.Equal("enabled", Assert.Single(functions.Entries).Key);

        Assert.All(
            ao.Structs.Where(one => one.Name != "Functions"),
            one => Assert.False(one.Client));
    }

    [Fact]
    public void AnEmptyStructIsAStructAndNotAFault()
    {
        AnimatedObject ao = AnimatedObject.Parse(Sample);

        AoStruct brackets = Assert.Single(ao.Named("Brackets"));
        Assert.Empty(brackets.Entries);
        Assert.Empty(ao.Faults);
    }

    /// <summary>
    /// Every malformed shape is reported, and none of them hangs or throws.
    /// </summary>
    /// <remarks>
    /// THE POINT OF THE READER IS TO MEASURE, so silence is the one unacceptable outcome. A
    /// hand-written scanner's characteristic bug is a branch that consumes nothing and loops
    /// forever; the theory arguments here each hit a different one of those branches, and xUnit
    /// failing on a hang is the check.
    /// </remarks>
    [Theory]
    [InlineData("Foo {\n\tx = 1\n}")]
    [InlineData("version\nFoo {}")]
    [InlineData("version 2\nFoo {\n\tx = 1\n")]
    [InlineData("version 2\nFoo {\n\tx = \"abc\n}")]
    [InlineData("version 3\nFoo {\n\tx = { a( \n}")]
    [InlineData("version 2\n/* hallo\nFoo {}")]
    [InlineData("version 2\n???\nFoo { }")]
    [InlineData("version 2\nextends bare.ao\nFoo {}")]
    [InlineData("version 3\nclient {\n\tFoo {}\n")]
    [InlineData("}}}}")]
    [InlineData("version 2\nFoo {\n\tjustakey\n\ty = 2\n}")]
    [InlineData("=")]
    [InlineData("{")]
    [InlineData("'")]
    public void NothingIsDroppedQuietly(string text)
    {
        AnimatedObject ao = AnimatedObject.Parse(text);

        Assert.NotEmpty(ao.Faults);
        Assert.All(ao.Faults, one => Assert.False(string.IsNullOrWhiteSpace(one)));
    }

    /// <summary>
    /// One unreadable line does not eat the struct after it.
    /// </summary>
    /// <remarks>
    /// THIS IS THE BUG THE STRUCT-NAME RULE EXISTS FOR. With the key rule applied to struct names,
    /// "???" was taken as a nameless struct, its opening brace was then found on the NEXT struct's
    /// line, and "Foo" disappeared into it - one line of rubbish costing a whole struct, with a
    /// fault that pointed at the wrong line. The reference spells struct names [A-Za-z0-9]+ and
    /// entry keys \S+, and that difference is load-bearing.
    /// </remarks>
    [Fact]
    public void RubbishBetweenTwoStructsCostsOnlyItself()
    {
        AnimatedObject ao = AnimatedObject.Parse("version 2\n???\nFoo { }\nBar { }");

        Assert.Equal(["Foo", "Bar"], ao.Structs.Select(one => one.Name));
        Assert.Contains(ao.Faults, one => one.Contains("???", StringComparison.Ordinal));
    }

    /// <summary>
    /// An unterminated comment is complained about once, not once per caller.
    /// </summary>
    /// <remarks>
    /// THE SYMPTOM THAT FOUND A REAL FLAW. Three identical faults for one comment meant the trivia
    /// skip was being rewound and re-run - the word matcher saved its position BEFORE skipping and
    /// restored it on a miss. Harmless-looking, but it re-scanned every comment in the file once
    /// per keyword tried. The count is the cheapest way to keep that fixed.
    /// </remarks>
    [Fact]
    public void AnUnclosedCommentIsReportedOnce()
    {
        AnimatedObject ao = AnimatedObject.Parse("version 2\n/* hallo\nFoo {}");

        Assert.Single(ao.Faults, one => one.Contains("comment", StringComparison.Ordinal));
    }

    /// <summary>
    /// A byte order mark is stepped over, and so is UTF-16 without one.
    /// </summary>
    /// <remarks>
    /// The format is "UTF16 with Byte Order Marker", and the decode is shared with the stat
    /// description files, which already handle the three shapes the game's text files come in.
    /// A mark left in the text would be part of the first token, and "﻿version" is not
    /// "version" - the file would read as having no version line at all.
    /// </remarks>
    [Fact]
    public void AByteOrderMarkDoesNotBecomePartOfTheFirstWord()
    {
        byte[] marked = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("version 2\nFoo { x = 1 }")];
        byte[] bare = Encoding.Unicode.GetBytes("version 2\nFoo { x = 1 }");

        foreach (byte[] content in new[] { marked, bare })
        {
            AnimatedObject ao = AnimatedObject.Read(content);
            Assert.Equal(2, ao.Version);
            Assert.Equal("Foo", Assert.Single(ao.Structs).Name);
            Assert.Empty(ao.Faults);
        }
    }

    /// <summary>
    /// An entry on the same line as its braces reads, which is where the references disagree.
    /// </summary>
    /// <remarks>
    /// zao's pattern for a value is (.*) - everything to the end of the line - and under that rule
    /// "Foo { x = 1 }" cannot be read at all: the value swallows the closing brace and the struct
    /// runs to the end of the file. The diagram says instead that the file "should be parsed
    /// token-wise rather than by line". A brace cannot occur inside a number or a bool, so
    /// stopping an unquoted value at one honours the stated rule without giving up the
    /// rest-of-line behaviour that keeps a trailing comment attached to its value.
    /// </remarks>
    [Theory]
    [InlineData("version 2\nFoo { x = 1 }", "1")]
    [InlineData("version 2\nFoo {\n\tx = 1\n}", "1")]
    [InlineData("version 2\nFoo {\n\tx = 1 // why\n}", "1 // why")]
    public void AnUnquotedValueStopsAtTheBraceOrTheLineEnd(string text, string value)
    {
        AnimatedObject ao = AnimatedObject.Parse(text);

        Assert.Empty(ao.Faults);
        AoEntry entry = Assert.Single(Assert.Single(ao.Structs).Entries);
        Assert.Equal("x", entry.Key);
        Assert.Equal(value, entry.Value);
    }

    [Fact]
    public void NothingToReadIsNoneRatherThanAThrow()
    {
        Assert.False(AnimatedObject.Read((byte[]?)null).Ready);
        Assert.False(AnimatedObject.Read([]).Ready);
        Assert.False(AnimatedObject.Read(null, "some/path.ao").Ready);
        Assert.False(AnimatedObject.Parse(null).Ready);
        Assert.False(AnimatedObject.Parse(string.Empty).Ready);
        Assert.Empty(AnimatedObject.None.Faults);
    }

    /// <summary>
    /// A script's paths are found inside its quotes, which is where the interesting ones are.
    /// </summary>
    /// <remarks>
    /// THE SURVEY COUNTS WHAT IT CAN SEE, so this decides what the measurement can report. A
    /// monster's effect packs and played objects are named inside <c>on_*</c> handlers -
    /// <c>PlayEffect("…/foo.ao")</c> - and a survey that only read plainly-quoted values would
    /// report that monsters reference almost nothing, which would look like an answer.
    /// </remarks>
    [Fact]
    public void AScriptsQuotedPathsAreFound()
    {
        AnimatedObject ao = AnimatedObject.Parse(Sample);
        AoStruct controller = Assert.Single(ao.Named("AnimationController"));

        string[] found = [.. AoSurvey.Referenced(controller.Entries[1])];

        Assert.Equal(["Metadata/Effects/foo.ao", "a/b.epk"], found);
    }

    /// <summary>
    /// A plainly quoted path is one reference; a number or a bare word is none.
    /// </summary>
    /// <remarks>
    /// AN EXTENSION AND A SLASH ARE BOTH REQUIRED, which keeps "0.75" and "stance2" out of the
    /// file-type count. Those two are exactly the values that would otherwise be reported as
    /// files with extensions ".75" and none - and a survey whose top file type is ".75" is
    /// measuring its own filter.
    /// </remarks>
    [Fact]
    public void OnlyThingsShapedLikePathsCountAsReferences()
    {
        AnimatedObject ao = AnimatedObject.Parse(Sample);
        AoStruct controller = Assert.Single(ao.Named("AnimationController"));

        Assert.Equal(
            ["Metadata/Monsters/Skeleton/Skeleton.amd"],
            AoSurvey.Referenced(controller.Entries[0]));

        // "stance2" is a name, "0.75" is a number. Neither is a file.
        Assert.Empty(AoSurvey.Referenced(controller.Entries[2]));
        Assert.Empty(AoSurvey.Referenced(controller.Entries[3]));
    }

    /// <summary>Nothing to walk is an empty report rather than a throw or a wrong zero.</summary>
    [Fact]
    public void ASurveyWithNoInstallSaysSoRatherThanReportingNothingFound()
    {
        AoSurveyResult none = AoSurvey.Read(null, null);

        Assert.Equal(0, none.Asked);
        Assert.Empty(none.Faults);
        Assert.Empty(none.Extensions);

        var said = new StringWriter();
        AoSurvey.Report(none, said);
        Assert.Contains("nothing to walk", said.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The two shapes a real install turned up that neither reference describes.
    /// </summary>
    /// <remarks>
    /// EVERY FAULT OF THE FIRST REAL SURVEY WAS THIS, 40 of 1687 files and all one message: a line
    /// beginning with a brace where an entry was expected. Two things produce it and the output
    /// could not say which, so both are read rather than one being picked:
    ///
    ///   1. A value whose brace is on the NEXT line. The key reads as an entry with an empty
    ///      native value and the brace is then orphaned; the script belongs to that entry.
    ///   2. A struct inside a struct, which only the client block was known to do.
    ///
    /// The entry AFTER the nested block is the part that would break quietly: it belongs to the
    /// outer struct, and a reader that swallowed the block without finding its close would put
    /// that entry, and every one after it, somewhere else.
    /// </remarks>
    [Fact]
    public void ABraceOnItsOwnLineIsEitherAScriptOrANestedStruct()
    {
        AnimatedObject ao = AnimatedObject.Parse("""
            version 3
            AnimationController
            {
            	on_death =
            	{
            		PlayEffect("Metadata/Effects/x.ao");
            		if (a) { b(); }
            	}
            	after_script = 1
            }

            AnimatedRender
            {
            	Sub {
            		x = 1
            	}
            	after_block = 2
            }
            """);

        Assert.Empty(ao.Faults);

        AoStruct controller = Assert.Single(ao.Named("AnimationController"));
        AoEntry script = controller.Entries[0];
        Assert.Equal("on_death", script.Key);
        Assert.Equal(AoValueKind.Script, script.Kind);
        Assert.Contains("PlayEffect", script.Value, StringComparison.Ordinal);
        Assert.Contains("if (a) { b(); }", script.Value, StringComparison.Ordinal);
        Assert.Equal("after_script", controller.Entries[1].Key);

        // The nested block stands beside its parent, and what followed it stayed with the parent.
        Assert.Equal("x", Assert.Single(Assert.Single(ao.Named("Sub")).Entries).Key);
        Assert.Equal("after_block", Assert.Single(Assert.Single(ao.Named("AnimatedRender")).Entries).Key);
    }

    /// <summary>
    /// A material is named with an index after its extension, and that is not part of the name.
    /// </summary>
    /// <remarks>
    /// THE FIRST SURVEY COUNTED 16 MATERIALS BESIDE 1683 MESHES, and one monster's dump showed
    /// fourteen materials on a single mesh. The count was not small - the filter was throwing
    /// them away, because the text after the last dot was "mat:0" and a colon is not a letter.
    /// A number the filter produced about itself is the worst kind: it looks like data.
    /// </remarks>
    [Theory]
    [InlineData("Art/Models/X/Textures/ExpeditionSkeleton.mat:0", ".mat", "Art/Models/X/Textures/ExpeditionSkeleton.mat")]
    [InlineData("Art/Models/X/Textures/S.mat:12", ".mat", "Art/Models/X/Textures/S.mat")]
    [InlineData("Art/Models/X/BasicSkeleton.sm", ".sm", "Art/Models/X/BasicSkeleton.sm")]
    [InlineData("Metadata/Monsters/Foo/Bar.ao", ".ao", "Metadata/Monsters/Foo/Bar.ao")]
    public void AnIndexAfterTheExtensionIsNotPartOfIt(string path, string extension, string bare)
    {
        Assert.Equal(extension, AoSurvey.Extension(path));
        Assert.Equal(bare, AoSurvey.Bare(path));
    }

    /// <summary>
    /// An animation's name lives inside the payload, not in the rare "animation" entry.
    /// </summary>
    /// <remarks>
    /// THE FIRST SURVEY FOUND EIGHT NAMES over a whole install, which is not a fact about the game
    /// - it is a fact about which key was read. The names are in the JSON of an "animations"
    /// entry, thousands of them, and that count is what the Stance question turns on.
    /// </remarks>
    [Fact]
    public void AnimationNamesComeOutOfThePayload()
    {
        const string Payload =
            """[ { "name": "attack_01a", "events": [] }, { "name" : "death_bwd_01", "x": 1 } ]""";

        Assert.Equal(["attack_01a", "death_bwd_01"], AoSurvey.Names(Payload));

        // A payload that is cut off, or is not JSON at all, yields what it can and never throws.
        Assert.Empty(AoSurvey.Names("""[ { "name": """));
        Assert.Empty(AoSurvey.Names("nothing here"));
        Assert.Empty(AoSurvey.Names(string.Empty));
    }

    /// <summary>
    /// A value may be written under its <c>=</c> instead of after it, for any of the three openers.
    /// </summary>
    /// <remarks>
    /// EVERY REMAINING FAULT OF THE SECOND REAL SURVEY WAS THIS - 26 files, all reading "not an
    /// entry in AnimatedRender", and the files say why: the "=" ends its line and the payload
    /// opens the next one. The first fix covered a brace, which was the shape in front of me at
    /// the time; a quote does the same and was left out, so the rule is written once for all
    /// three rather than a third time when the next one turns up.
    /// </remarks>
    [Theory]
    [InlineData("'{ \"passes\": [] }'", AoValueKind.Payload)]
    [InlineData("\"Art/Models/x.sm\"", AoValueKind.Quoted)]
    [InlineData("{ Play(\"a/b.ao\"); }", AoValueKind.Script)]
    public void AValueMayOpenOnTheLineAfterItsEquals(string value, AoValueKind kind)
    {
        AnimatedObject ao = AnimatedObject.Parse(
            $"version 3\nAnimatedRender\n{{\n\tRenderPasses = \n\t{value}\n\tafter = 1\n}}");

        Assert.Empty(ao.Faults);

        AoStruct block = Assert.Single(ao.Structs);
        Assert.Equal(2, block.Entries.Count);
        Assert.Equal("RenderPasses", block.Entries[0].Key);
        Assert.Equal(kind, block.Entries[0].Kind);

        // And the entry after it is still the outer struct's, which is what would break quietly.
        Assert.Equal("after", block.Entries[1].Key);
    }

    /// <summary>
    /// A bare word on the next line is NOT a value - it is the next entry.
    /// </summary>
    /// <remarks>
    /// The other half of the rule above, and the reason it is limited to the three openers: an
    /// unquoted value really is the rest of its own line, so reaching onto the next one would
    /// swallow whatever is there.
    /// </remarks>
    [Fact]
    public void AnEmptyValueDoesNotEatTheNextEntry()
    {
        AnimatedObject ao = AnimatedObject.Parse("version 3\nFoo\n{\n\tempty = \n\tnext = 2\n}");

        Assert.Empty(ao.Faults);
        AoStruct block = Assert.Single(ao.Structs);
        Assert.Equal(["empty", "next"], block.Entries.Select(one => one.Key));
        Assert.Equal(string.Empty, block.Entries[0].Value);
        Assert.Equal("2", block.Entries[1].Value);
    }

    /// <summary>
    /// A quoted value may carry a socket name before its path - and a path may contain a space.
    /// </summary>
    /// <remarks>
    /// TWO SHAPES, AND THE SECOND RULES OUT THE OBVIOUS READING OF THE FIRST. Every attachment in
    /// the game names a socket and then a file inside one pair of quotes, so taking the value
    /// whole asks for a path no install has - the second survey found 1852 of 3262 files, and the
    /// 2289 attachments were failing as a body. But splitting on whitespace and keeping the
    /// path-shaped pieces destroys "Audio/Sound Effects/…", which is a real folder: every .ogg in
    /// a ten-file sample vanished at once when that was tried.
    ///
    /// So the rule is the slash: a socket is a bone or a slot and never has one, and everything
    /// from the first word that does belongs to the file. The remark on the old filter claimed
    /// "no spaces in it" while the code never checked, and making the code agree with the comment
    /// is what surfaced the second shape - the comment was wrong about the game and the code had
    /// been right by omission.
    /// </remarks>
    [Theory]
    [InlineData(
        "eye_L Metadata/Effects/Spells/undead_summon_skeletons/eye_glow.ao",
        "Metadata/Effects/Spells/undead_summon_skeletons/eye_glow.ao")]
    [InlineData(
        "<root> Metadata/Monsters/Skeletons/PlayerSummoned/Warrior/attachments/Armour.ao",
        "Metadata/Monsters/Skeletons/PlayerSummoned/Warrior/attachments/Armour.ao")]
    [InlineData(
        "Audio/Sound Effects/MonsterSounds/MudBurrower/BodyBarrierIdle.loop.ogg",
        "Audio/Sound Effects/MonsterSounds/MudBurrower/BodyBarrierIdle.loop.ogg")]
    [InlineData("Art/Models/X/Textures/S.mat:0", "Art/Models/X/Textures/S.mat:0")]
    [InlineData("grp_head false aux_target_head_01 aux_target_head_02", "")]
    [InlineData("0 0 -9.35201", "")]
    [InlineData("aux_target_head_01", "")]
    [InlineData("true", "")]
    [InlineData("", "")]
    public void APathStartsAtTheFirstWordWithASlashInIt(string value, string path)
        => Assert.Equal(path, AoSurvey.Path(value));

    /// <summary>Walking a struct's entries reaches the children too, in file order.</summary>
    [Fact]
    public void WalkingReachesTheChildren()
    {
        AnimatedObject ao = AnimatedObject.Parse(Sample);

        string[] keys = [.. ao.Entries()
            .Where(one => one.Struct.Name == "AnimationController")
            .Select(one => one.Entry.Key)];

        Assert.Equal(["metdata", "on_event", "animation", "0.35", "1,2", "blend"], keys);
    }
}
