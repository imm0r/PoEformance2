using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// The markup the game writes its own texts in, against lines taken from the table itself.
/// </summary>
/// <remarks>
/// Every string here was read out of KeywordPopups in the dissector, doubled percent signs
/// and all. That matters more than it looks: both rules this renderer implements were derived
/// from what the column actually holds, so a test written from invented examples would be
/// checking the renderer against the theory it came from rather than against the game.
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
                + "Default value is +100%% (i.e. twice as much damage)."));

        Assert.Equal(
            "Jagged Ground Slows the movement speed of enemies in its area by 20%.",
            KeywordGlossary.Plain(
                "Jagged Ground [Slow|Slows] the movement speed of enemies in its area by 20%%."));
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
        Assert.Equal("Burning enemies", KeywordGlossary.Plain("[Burning] enemies"));
        Assert.Equal(["Burning"], KeywordGlossary.KeysIn("[Burning] enemies"));
    }

    [Fact]
    public void NothingIsNotAFailure()
    {
        Assert.Equal(string.Empty, KeywordGlossary.Plain(null));
        Assert.Equal(string.Empty, KeywordGlossary.Plain(string.Empty));
        Assert.Empty(KeywordGlossary.KeysIn(null));
    }
}
