using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// Where the flask keys come from, and what happens when they cannot be read.
/// </summary>
/// <remarks>
/// This is the question "which key would you press?" made testable. The answer must never
/// be an assumption dressed up as a fact: a binding that could not be mapped has to stay
/// unmapped, so the slot reports itself as unusable instead of pressing a number key the
/// player never bound to a flask.
/// </remarks>
public class FlaskKeyBindingTests
{
    [Fact]
    public void ReadsTheSpellingVariantsTheGameHasBeenSeenToWrite()
    {
        FlaskKeys keys = FlaskKeyBindings.Parse([
            "[ActionKeys]",
            "flask1=DIK_1",
            "flask_2 = KEY_Q",
            "UseFlask3=F5",
            "Input_flask_4_primary = DIK_NUMPAD4",
        ]);

        Assert.Equal(KeyBindingSource.GameConfig, keys.Source);
        Assert.Equal(0x31, keys.BySlot[1]);        // 1
        Assert.Equal(0x51, keys.BySlot[2]);        // Q
        Assert.Equal(0x74, keys.BySlot[3]);        // F5
        Assert.Equal(0x64, keys.BySlot[4]);        // Numpad 4
        Assert.Equal(0x35, keys.BySlot[5]);        // untouched, so the default stands
    }

    [Fact]
    public void ContentThatDoesNotParse_IsTheOneWorthWarningAbout()
    {
        // Something IS in the file and none of it was understood. That is the case where
        // the tool might be pressing a key the player rebound, so it must be told apart
        // from an empty file - where the same 1-5 fallback is simply correct.
        FlaskKeys keys = FlaskKeyBindings.Parse(["[Sound]", "volume=50"], source: "test.ini");

        Assert.Equal(KeyBindingSource.Unmatched, keys.Source);
        Assert.Equal(FlaskKeyBindings.Defaults, keys.BySlot);
        Assert.Contains("test.ini", keys.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]                    // truly empty
    [InlineData("﻿")]              // a byte-order mark and nothing else - the real case
    [InlineData("; just a comment")]
    public void EmptyConfig_IsReportedAsTheGameUsingItsOwnDefaults(string line)
    {
        // A real player's config came back as three bytes: a UTF-8 BOM. The game writes
        // this file when a binding is CHANGED, so someone who never rebound a flask has
        // nothing in it - and is running the same 1-5 layout the fallback assumes. Calling
        // that a parse failure sends the reader hunting a bug that does not exist.
        FlaskKeys keys = FlaskKeyBindings.Parse([line], source: "test.ini");

        Assert.Equal(KeyBindingSource.GameDefaults, keys.Source);
        Assert.Equal(FlaskKeyBindings.Defaults, keys.BySlot);
        Assert.Contains("empty", keys.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NumericValueIsAVirtualKeyCode_NotADigit()
    {
        // The one that cannot be guessed, and the reason for reading the AHK tool rather
        // than inventing a parser: the game writes decimal VIRTUAL-KEY CODES for keys it
        // has no name for. "81" is Q. Reading it as the 8 and 1 keys would bind the flask
        // to something the player has never touched.
        Assert.Equal(0x51, FlaskKeyBindings.ToVirtualKey("81"));    // Q
        Assert.Equal(0x74, FlaskKeyBindings.ToVirtualKey("116"));   // F5
        Assert.Equal(0x62, FlaskKeyBindings.ToVirtualKey("98"));    // Numpad 2

        // Single digits are codes too, and a real config is what settled it: the same file
        // holds use_flask_in_slot1=49 and use_bound_skill1=1, and the second is the primary
        // skill on LEFT MOUSE. So "1" is a mouse button - unusable, and reported as such -
        // rather than the 1 key.
        Assert.Equal(0, FlaskKeyBindings.ToVirtualKey("1"));
        Assert.Equal(0, FlaskKeyBindings.ToVirtualKey("2"));
    }

    [Fact]
    public void AnUnboundSlotIsUnboundAndNotTheZeroKey()
    {
        // Found by running the parser over a real config: three flask slots the player had
        // never bound came back as the ZERO KEY, so the tool would have pressed something
        // they never chose. That is the exact failure this class exists to prevent, arrived
        // at through the code meant to prevent it.
        Assert.Equal(0, FlaskKeyBindings.ToVirtualKey("0"));

        FlaskKeys keys = FlaskKeyBindings.Parse(["use_flask_in_slot3=0"]);
        Assert.Equal(0, keys.BySlot[3]);
        Assert.Equal("unbound", FlaskKeyBindings.Describe(keys.BySlot[3]));
    }

    [Fact]
    public void UnmappableBinding_LeavesTheSlotUnusableRatherThanGuessingAKey()
    {
        // A flask on a mouse button or a bare modifier is the case this protects: there is
        // no keystroke that stands in for either, so the slot must report that it cannot
        // fire. Leaving the default "1" in place would press a key the player never bound
        // to that flask - worse than doing nothing, because it is silent and it does
        // something.
        Assert.Equal(0, FlaskKeyBindings.ToVirtualKey("MOUSE4"));
        Assert.Equal(0, FlaskKeyBindings.ToVirtualKey("16"));      // VK_SHIFT
        Assert.Equal(0, FlaskKeyBindings.ToVirtualKey(""));

        FlaskKeys keys = FlaskKeyBindings.Parse(["flask2=MOUSE4"]);

        Assert.Equal(KeyBindingSource.GameConfig, keys.Source);     // the line WAS read
        Assert.Equal(0, keys.BySlot[2]);                            // ...and it is unusable
        Assert.Equal(0x31, keys.BySlot[1]);                         // untouched slots keep the game default
    }

    [Fact]
    public void ValueIsReadUpToTheFirstAlternateOrComment()
    {
        Assert.Equal(0x31, FlaskKeyBindings.ToVirtualKey("DIK_1, DIK_NUMPAD1"));

        // A letter, not a digit: what this is about is where the value ENDS, and a bare
        // quoted digit is separately ambiguous - it could be a name or a code - so leaning on
        // it here would make this test fail for a reason that has nothing to do with it.
        Assert.Equal(0x51, FlaskKeyBindings.ToVirtualKey("\"Q\" ; primary"));
    }

    [Fact]
    public void MissingFile_ReportsThePathItLookedIn()
    {
        // A tool that says "using defaults" without saying where it looked leaves the one
        // useful next step - "is that where my config actually is?" - unanswerable.
        FlaskKeys keys = FlaskKeyBindings.Load(Path.Combine(Path.GetTempPath(), "poeformance-no-such-config.ini"));

        Assert.Equal(KeyBindingSource.GameDefaults, keys.Source);
        Assert.Contains("poeformance-no-such-config.ini", keys.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_SaysEmptyRatherThanLeavingItToGuesswork()
    {
        // The diagnostic that would have answered this in one run instead of a round trip.
        string path = Path.Combine(Path.GetTempPath(), $"poeformance-cfg-{Guid.NewGuid():N}.ini");
        try
        {
            File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF]);   // exactly what the real file held

            var text = new StringWriter();
            KeyBindingProbe.Report(text, path);

            Assert.Contains("3 bytes", text.ToString(), StringComparison.Ordinal);
            Assert.Contains("EMPTY", text.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Probe_ShowsWhatEachLineReadsAsWhenNothingParsed()
    {
        // The case that needs the dump: content is there, no flask binding was found. What
        // the reader needs is the key NAMES the game used, and how each value decodes.
        string path = Path.Combine(Path.GetTempPath(), $"poeformance-cfg-{Guid.NewGuid():N}.ini");
        try
        {
            File.WriteAllLines(path, ["[ACTION_KEYS]", "use_bound_skill4=81 2", "user_input_mode=wasd"]);

            var text = new StringWriter();
            KeyBindingProbe.Report(text, path);
            string report = text.ToString();

            Assert.Contains("[ACTION_KEYS]", report, StringComparison.Ordinal);
            Assert.Contains("use_bound_skill4", report, StringComparison.Ordinal);
            Assert.Contains("-> Q", report, StringComparison.Ordinal);      // 81, weapon-set suffix dropped
            Assert.Contains("Unmatched", report, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Describe_NamesTheKeyRatherThanItsCode()
    {
        Assert.Equal("1", FlaskKeyBindings.Describe(0x31));
        Assert.Equal("Numpad 4", FlaskKeyBindings.Describe(0x64));
        Assert.Equal("F5", FlaskKeyBindings.Describe(0x74));
        Assert.Equal("unbound", FlaskKeyBindings.Describe(0));
    }
}

/// <summary>
/// Reading the binding set the game is ACTUALLY using, and finding the file at all.
/// </summary>
/// <remarks>
/// Both of these came out of one real config. The file holds two complete sets of key
/// bindings and only one is live; and it was not where the tool looked, because OneDrive had
/// moved Documents and renamed it into the user's own language. Either one on its own is a
/// silent failure - the tool presses the game's default keys and nothing says why.
/// </remarks>
public class FlaskKeyBindingSectionTests
{
    /// <summary>
    /// The shape of a real config: an input mode, then two sets of bindings that DISAGREE.
    /// </summary>
    /// <remarks>
    /// Written out rather than shipped as a fixture, because a real one carries the player's
    /// account name. The values are made to differ on purpose - in the config that prompted
    /// this they happened to agree about flasks, which is exactly why choosing the wrong
    /// section could go unnoticed.
    /// </remarks>
    private static string[] Config(string inputMode) =>
    [
        "[GENERAL]",
        $"user_input_mode={inputMode}",
        "[WASD_ACTION_KEYS]",
        "use_flask_in_slot1=81",          // Q
        "use_flask_in_slot2=69",          // E
        "[LANGUAGE]",
        "chat_language=de",
        "[ACTION_KEYS]",
        "use_flask_in_slot1=49",          // 1
        "use_flask_in_slot2=50",          // 2
    ];

    [Fact]
    public void AWasdPlayerGetsTheWasdBindings()
    {
        FlaskKeys keys = FlaskKeyBindings.Parse(Config("wasd"));

        Assert.Equal(0x51, keys.BySlot[1]);
        Assert.Equal(0x45, keys.BySlot[2]);
        Assert.Contains("WASD_ACTION_KEYS", keys.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void EverybodyElseGetsTheOrdinaryOnes()
    {
        // And this is the case that would have passed either way if the sections agreed -
        // which is why the fixture makes them disagree.
        FlaskKeys keys = FlaskKeyBindings.Parse(Config("mouse"));

        Assert.Equal(0x31, keys.BySlot[1]);
        Assert.Equal(0x32, keys.BySlot[2]);
    }

    [Fact]
    public void ASectionOrderThatPutsTheLIVEOneFirstStillWorks()
    {
        // The old reading took whichever came last, so it happened to be right for one mode
        // and wrong for the other purely by where the game writes the sections.
        string[] reversed =
        [
            "[GENERAL]",
            "user_input_mode=wasd",
            "[ACTION_KEYS]",
            "use_flask_in_slot1=49",
            "[WASD_ACTION_KEYS]",
            "use_flask_in_slot1=81",
        ];

        Assert.Equal(0x51, FlaskKeyBindings.Parse(reversed).BySlot[1]);
    }

    [Fact]
    public void AnUnknownInputModeStillUsesWhatIsThere()
    {
        // A future mode must not turn into "no bindings at all" - the ordinary section is
        // still far closer to right than the built-in defaults.
        FlaskKeys keys = FlaskKeyBindings.Parse(Config("something_new"));

        Assert.Equal(KeyBindingSource.GameConfig, keys.Source);
        Assert.Equal(0x31, keys.BySlot[1]);
    }

    [Fact]
    public void AConfigWithNoSectionsAtAllIsStillRead()
    {
        // Older files, and every test written before sections existed.
        FlaskKeys keys = FlaskKeyBindings.Parse(["use_flask_in_slot1=81"]);

        Assert.Equal(KeyBindingSource.GameConfig, keys.Source);
        Assert.Equal(0x51, keys.BySlot[1]);
    }

    [Fact]
    public void SlotsTheConfigDoesNotMentionKeepTheGamesOwnDefault()
    {
        FlaskKeys keys = FlaskKeyBindings.Parse(Config("wasd"));

        Assert.Equal(0x33, keys.BySlot[3]);
    }

    [Fact]
    public void TheConfigIsLookedForUnderTheDocumentsFolderGivenToIt()
    {
        // The path the game uses, built from whichever Documents folder is handed in - which
        // is what lets a redirected, renamed one be tried without knowing its name.
        string path = FlaskKeyBindings.ConfigUnder(Path.Combine("C:", "Users", "x", "OneDrive", "Dokumente"));

        Assert.EndsWith(Path.Combine("My Games", "Path of Exile 2", "poe2_production_Config.ini"), path, StringComparison.Ordinal);
        Assert.Contains("Dokumente", path, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSearchOffersMoreThanTheOnePlaceWindowsReports()
    {
        // The whole fix in one assertion: looking only where Windows says Documents is was
        // what missed a config sitting in OneDrive under a translated name.
        List<string> candidates = [.. FlaskKeyBindings.CandidateConfigPaths()];

        Assert.Contains(FlaskKeyBindings.DefaultConfigPath, candidates);
        Assert.True(candidates.Count > 1, "one path is what caused the problem");
        Assert.All(candidates, path => Assert.EndsWith("poe2_production_Config.ini", path, StringComparison.Ordinal));
    }
}
