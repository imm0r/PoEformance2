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
    public void SaysWhenItFellBackToTheDefaultLayout()
    {
        // The difference that matters: "read from the game" and "assumed" produce the same
        // keys on a default setup and completely different behaviour on a rebound one. The
        // source is what lets the tool report which of the two it is looking at.
        FlaskKeys keys = FlaskKeyBindings.Parse(["[Sound]", "volume=50"], source: "test.ini");

        Assert.Equal(KeyBindingSource.Defaults, keys.Source);
        Assert.Equal(FlaskKeyBindings.Defaults, keys.BySlot);
        Assert.Contains("test.ini", keys.Detail, StringComparison.Ordinal);
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

        // A lone digit is the exception: "UseFlask3=3" means the 3 key. Read as a code it
        // would be VK 3, which is not something that can be pressed for a flask.
        Assert.Equal(0x33, FlaskKeyBindings.ToVirtualKey("3"));
        Assert.Equal(0x31, FlaskKeyBindings.ToVirtualKey("1"));
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
        Assert.Equal(0x31, FlaskKeyBindings.ToVirtualKey("\"1\" ; primary"));
    }

    [Fact]
    public void MissingFile_ReportsThePathItLookedIn()
    {
        // A tool that says "using defaults" without saying where it looked leaves the one
        // useful next step - "is that where my config actually is?" - unanswerable.
        FlaskKeys keys = FlaskKeyBindings.Load(Path.Combine(Path.GetTempPath(), "poeformance-no-such-config.ini"));

        Assert.Equal(KeyBindingSource.Defaults, keys.Source);
        Assert.Contains("poeformance-no-such-config.ini", keys.Detail, StringComparison.Ordinal);
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
