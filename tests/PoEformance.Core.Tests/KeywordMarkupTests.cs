using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// The markup the game writes its own texts in, against lines taken from the table itself.
/// </summary>
/// <remarks>
/// Every string here is a definition as the LIVE TABLE holds it - see GlossaryTableTests,
/// which asserts the same behaviour over all 1026 rows at once. That distinction cost a rule:
/// these examples were first transcribed from a dissector window, where "+100%" appears as
/// "+100%%" because our own text path doubles a percent for ImGui, and this file used to assert
/// that the renderer collapsed it. A test written from a screenshot checks the tool against
/// itself.
/// </remarks>
public class KeywordMarkupTests
{
    [Fact]
    public void MarkupDrawsTheHalfAfterThePipe()
    {
        Assert.Equal(
            "Multiplies the damage dealt by Critical Hits. Default value is +100% (i.e. twice as much damage).",
            KeywordGlossary.Plain(
                "Multiplies the damage dealt by [Critical|Critical Hits]. "
                + "Default value is +100% (i.e. twice as much damage)."));

        Assert.Equal(
            "Jagged Ground Slows the movement speed of enemies in its area by 20%.",
            KeywordGlossary.Plain(
                "Jagged Ground [Slow|Slows] the movement speed of enemies in its area by 20%."));
    }

    [Fact]
    public void AndAPercentIsLeftExactlyAsItIs()
    {
        // Nothing here escapes anything. The drawing code does that, at the point of drawing.
        Assert.Equal("50% less", KeywordGlossary.Plain("50% less"));
        Assert.Equal("a literal %% pair", KeywordGlossary.Plain("a literal %% pair"));
    }

    [Fact]
    public void AndTheKeysAreWhatIsLEFTOfIt()
    {
        // The left half is the engine Id and the right half is localised, so a lookup keyed on
        // what the player reads would work in English and fail in every other client.
        Assert.Equal(
            ["Allies", "Minion"],
            KeywordGlossary.KeysIn(
                "Totems are [Allies|allied] constructs which use skills for you. "
                + "Totems are not [Minion|Minions] and their skills benefit from your stats."));
    }

    [Fact]
    public void AKeyIsListedOnceHoweverOftenItAppears()
    {
        Assert.Equal(
            ["Critical"],
            KeywordGlossary.KeysIn("[Critical|Critical Hits] and more [Critical|Critical Hits]"));
    }

    [Fact]
    public void TextWithoutMarkupIsNotEvenCopied()
    {
        // Nearly every line the game has, so it is worth not building a StringBuilder for it.
        const string Plain = "Enemies you Taunt can only target you.";
        Assert.Same(Plain, KeywordGlossary.Plain(Plain));
        Assert.Empty(KeywordGlossary.KeysIn(Plain));
    }

    [Fact]
    public void AnUnbalancedBracketKeepsItsText()
    {
        // Not a case seen in the table. The rule is to lose no text, because a renderer that
        // silently drops the rest of a sentence is worse than one that shows a stray bracket.
        Assert.Equal("Enemies taking [Fire damage", KeywordGlossary.Plain("Enemies taking [Fire damage"));
        Assert.Empty(KeywordGlossary.KeysIn("Enemies taking [Fire damage"));
    }

    [Fact]
    public void ABracketWithNoPipeKeepsWhatIsInside()
    {
        // NOT a defensive guess, which is what the comment here used to call it: 859 of the
        // brackets in the live table have no pipe - "[Armour]", "[Bleeding]", "[Ignite]" - and
        // the word inside is itself a row Id. It is the ordinary form, not the odd one.
        Assert.Equal("Burning enemies", KeywordGlossary.Plain("[Burning] enemies"));
        Assert.Equal(["Burning"], KeywordGlossary.KeysIn("[Burning] enemies"));

        Assert.Equal(
            "Being Blinded causes 20% less Accuracy Rating",
            KeywordGlossary.Plain("Being Blinded causes 20% less [Accuracy|Accuracy Rating]"));
    }

    [Fact]
    public void TheRichTextMarkupPassesThrough()
    {
        // A second markup shares the column, in the 33 Expedition rune rows. Stripping it would
        // throw away the colours it exists to carry, so it is left for whatever draws it.
        const string Rune = "<rgb(219,217,206)>{Fire Rune}\r\n<italic>{Monsters gain:}";
        Assert.Same(Rune, KeywordGlossary.Plain(Rune));
        Assert.Empty(KeywordGlossary.KeysIn(Rune));
    }

    [Fact]
    public void NothingIsNotAFailure()
    {
        Assert.Equal(string.Empty, KeywordGlossary.Plain(null));
        Assert.Equal(string.Empty, KeywordGlossary.Plain(string.Empty));
        Assert.Empty(KeywordGlossary.KeysIn(null));
    }
}
