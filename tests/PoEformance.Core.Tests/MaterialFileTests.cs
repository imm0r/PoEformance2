using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// The material reader - the hop from a mesh to the textures that go on it.
/// </summary>
/// <remarks>
/// THIS IS WHAT ANSWERED "IS THERE A PICTURE". A survey of the monsters' .ao files found thirteen
/// referenced file types and no texture among them, which reads like "there is none" and was not:
/// an .ao names a .sm, a .sm names a .mat, and only the .mat names a .dds.
///
/// THE TWO SHAPES BELOW ARE BOTH REAL and the second is why the reader looks in two places. Of the
/// two materials checked against the game, one lists its textures in a "textures" array AND
/// assigns them to slots; the other has NO textures array at all and only the slots. A reader
/// written against the first alone comes back empty on the second, with nothing to report.
/// </remarks>
public class MaterialFileTests
{
    /// <summary>The shape of BasicSkeleton's material: a texture list and slot assignments.</summary>
    private const string Both = """
        {"version":4,
         "textures":[
           {"filename":"Art/Textures/Monsters/KatarinaSkeleton/BasicSkeleton_colour_DXT1.dds","format":"DXT1","count":1},
           {"filename":"Art/Textures/Monsters/KatarinaSkeleton/BasicSkeleton_normal_DXT5.dds","format":"DXT5","count":1}],
         "graphinstances":[
           {"parent":"Metadata/Materials/DielectricSpecGloss.fxgraph",
            "custom_parameters":[
              {"name":"AlbedoTransparency_TEX","parameters":[{"path":"Art/Textures/Monsters/KatarinaSkeleton/BasicSkeleton_colour_DXT1.dds","srgb":true},{}]},
              {"name":"NormalGlossAO_TEX","parameters":[{"path":"Art/Textures/Monsters/KatarinaSkeleton/BasicSkeleton_normal_DXT5.dds","srgb":false},{}]},
              {"name":"UseRoughness","parameters":[{"value":true}]}]}]}
        """;

    /// <summary>The shape of SkeletonVarc's: slots only, no texture list, decorated names.</summary>
    private const string SlotsOnly = """
        {"version":4,
         "graphinstances":[
           {"custom_parameters":[
             {"name":"AlbedoTransparency_TEX","parameters":[{"path":"Art/Textures/S_colour.dds"}]},
             {"name":"- Glow_TEX","parameters":[{"path":"Art/Textures/S_glow_BC1.dds"}]},
             {"name":"00. Intensity","parameters":[{"value":18.0}]},
             {"name":"05. Noise Scroll","parameters":[{"value":[0.05,0.5]}]}]}]}
        """;

    [Fact]
    public void AMaterialNamesItsTexturesAndWhatEachOneIsFor()
    {
        MaterialFile said = MaterialFile.Parse(Both);

        Assert.True(said.Ready);
        Assert.Equal(2, said.Textures.Count);
        Assert.Equal("DXT1", said.Textures[0].Format);
        Assert.Equal("DXT5", said.Textures[1].Format);

        Assert.Equal(
            "Art/Textures/Monsters/KatarinaSkeleton/BasicSkeleton_colour_DXT1.dds",
            said.Slots["AlbedoTransparency_TEX"]);
        Assert.Equal(
            "Art/Textures/Monsters/KatarinaSkeleton/BasicSkeleton_normal_DXT5.dds",
            said.Slots["NormalGlossAO_TEX"]);

        // A parameter with no path is a number rather than a texture and is not a slot.
        Assert.DoesNotContain("UseRoughness", said.Slots.Keys);
    }

    /// <summary>
    /// A material with no texture list still says what its textures are.
    /// </summary>
    /// <remarks>
    /// THE VARIANT THAT WOULD HAVE BEEN MISSED. Reading only the "textures" array is the obvious
    /// implementation and it comes back empty here - the second of two real materials checked, so
    /// not a rare corner. The slots carry the same paths and carry what each is for besides.
    /// </remarks>
    [Fact]
    public void AMaterialWithNoTextureListIsStillRead()
    {
        MaterialFile said = MaterialFile.Parse(SlotsOnly);

        Assert.True(said.Ready);
        Assert.Empty(said.Textures);
        Assert.Equal("Art/Textures/S_colour.dds", said.Albedo);

        // The leading punctuation of "- Glow_TEX" is cut; the rest is kept as the game writes it.
        Assert.Equal("Art/Textures/S_glow_BC1.dds", said.Slots["Glow_TEX"]);
    }

    /// <summary>
    /// The slot decides which texture is the skin, and a normal map is never it.
    /// </summary>
    /// <remarks>
    /// A NORMAL MAP DRAWN AS COLOUR PRODUCES A LILAC MONSTER, which reads as a shading bug and
    /// would be hunted in the renderer rather than here. The slot name says what a texture is for
    /// in the game's own words; the fall-back for a material with no useful slot is the first
    /// texture that is not obviously a normal map, and that is a guess rather than a reading.
    /// </remarks>
    [Fact]
    public void TheAlbedoIsTheColourTextureAndNotTheNormalMap()
    {
        Assert.EndsWith("_colour_DXT1.dds", MaterialFile.Parse(Both).Albedo, StringComparison.Ordinal);

        // With no slots at all, the normal map is stepped over rather than taken because it is first.
        MaterialFile bare = MaterialFile.Parse(
            """{"textures":[{"filename":"a/b_normal_DXT5.dds"},{"filename":"a/b_colour.dds"}]}""");

        Assert.Equal("a/b_colour.dds", bare.Albedo);
    }

    /// <summary>
    /// The number after a material picks one of its graphs, and each graph has its own colour map.
    /// </summary>
    /// <remarks>
    /// THE REMARK ON <c>Bare</c> SAID THIS ALL ALONG - "the number picks within the file" - and
    /// nothing acted on it: the graphs were merged into one dictionary and every shape of a
    /// monster read the FIRST colour map in the file. On a monster built of parts that paints
    /// the head's sheet onto the cloak, which is patches of the wrong colour rather than a
    /// missing texture, and it was reported from the live client on three bosses in a row.
    ///
    /// THE FALLBACK IS THE OTHER HALF. A selector past the end, or one pointing at a graph with
    /// no colour map in it, leaves the shape with the file's own answer - which is what it had
    /// before any of this, so a material shaped in a way this does not understand is no worse
    /// off than it was.
    /// </remarks>
    [Fact]
    public void ASelectorPicksTheGraphItNumbers()
    {
        MaterialFile said = MaterialFile.Parse(
            """
            {"graphinstances":[
              {"custom_parameters":[
                {"name":"AlbedoTransparency_TEX","parameters":[{"path":"art/head_colour.dds"}]}]},
              {"custom_parameters":[
                {"name":"AlbedoTransparency_TEX","parameters":[{"path":"art/cloak_colour.dds"}]}]},
              {"custom_parameters":[
                {"name":"NormalGlossAO_TEX","parameters":[{"path":"art/rim_normal.dds"}]}]}
            ]}
            """);

        Assert.Equal(3, said.Graphs.Count);
        Assert.Equal("art/head_colour.dds", said.AlbedoAt(0));
        Assert.Equal("art/cloak_colour.dds", said.AlbedoAt(1));

        // The merged view is unchanged for anything with no selector to go on: the first
        // colour map in the file.
        Assert.Equal("art/head_colour.dds", said.Albedo);
        Assert.Equal("art/head_colour.dds", said.AlbedoAt(-1));

        // A graph with no colour map of its own, and one past the end: the file's answer.
        Assert.Equal("art/head_colour.dds", said.AlbedoAt(2));
        Assert.Equal("art/head_colour.dds", said.AlbedoAt(9));
    }

    [Theory]
    [InlineData("art/Boss.mat:0", 0)]
    [InlineData("art/Boss.mat:1", 1)]
    [InlineData("art/Boss.mat:12", 12)]
    [InlineData("art/Boss.mat", -1)]
    [InlineData("art/Boss:name.mat", -1)]
    [InlineData("", -1)]
    [InlineData(null, -1)]
    public void TheSelectorIsReadOffThePath(string? path, int at)
    {
        Assert.Equal(at, MaterialFile.SelectorOf(path));

        // And the two halves agree about where the colon is: what Bare cuts off is what
        // SelectorOf reads, so a path cannot be a file to one of them and not to the other.
        Assert.Equal(at >= 0, MaterialFile.Bare(path) != (path ?? string.Empty).Trim());
    }

    /// <summary>
    /// A colour map chosen because nothing SAID it is one is reported as the guess it is.
    /// </summary>
    /// <remarks>
    /// The fallback - the first texture that is not a normal map - is right on the materials
    /// that have been looked at and lands on a mask or an occlusion map when it is not. That
    /// draws the monster in greyscale, which reads as "the texture is missing" and sends
    /// somebody hunting for a file that is not lost. See MonsterModel.Guessed.
    /// </remarks>
    [Fact]
    public void AGuessedColourMapSaysItWasGuessed()
    {
        MaterialFile named = MaterialFile.Parse(
            """{"graphinstances":[{"custom_parameters":[{"name":"AlbedoTransparency_TEX","parameters":[{"path":"a/b_colour.dds"}]}]}]}""");

        Assert.Equal(("a/b_colour.dds", true), named.AlbedoOf(0));

        MaterialFile guessed = MaterialFile.Parse(
            """{"textures":[{"filename":"a/b_normal_DXT5.dds"},{"filename":"a/b_mask.dds"}]}""");

        Assert.Equal(("a/b_mask.dds", false), guessed.AlbedoOf(-1));
    }

    /// <summary>
    /// A trailing comma is legal here and throws in the parser's default settings.
    /// </summary>
    /// <remarks>
    /// THE SPEC SAYS THEY OCCUR and neither of the two materials checked against the game has one,
    /// which is exactly the sort of thing a sample cannot settle. Without the option, some
    /// fraction of monsters would have had no skin and nothing would have said why.
    /// </remarks>
    [Fact]
    public void ATrailingCommaIsRead()
    {
        MaterialFile said = MaterialFile.Parse(
            """{"textures":[{"filename":"a/b.dds","format":"DXT1",},],}""");

        Assert.True(said.Ready);
        Assert.Equal("a/b.dds", Assert.Single(said.Textures).Path);
    }

    /// <summary>
    /// A parameter's value is a number in some materials and a list in others, and neither breaks it.
    /// </summary>
    /// <remarks>
    /// THE REASON THIS IS SCANNED RATHER THAN DESERIALISED. A type faithful to that field is a lot
    /// of shape for two strings, and a reader that skips what it does not want cannot trip on it.
    /// </remarks>
    [Theory]
    [InlineData("""{"graphinstances":[{"custom_parameters":[{"name":"A_TEX","parameters":[{"value":0.5},{"path":"a/b.dds"}]}]}]}""")]
    [InlineData("""{"graphinstances":[{"custom_parameters":[{"name":"A_TEX","parameters":[{"value":[0.1,0.5]},{"path":"a/b.dds"}]}]}]}""")]
    public void AValueThatChangesShapeDoesNotStopThePath(string text)
        => Assert.Equal("a/b.dds", MaterialFile.Parse(text).Slots["A_TEX"]);

    /// <summary>
    /// Anything that is not a material comes back empty rather than throwing.
    /// </summary>
    /// <remarks>
    /// THE MESH IS ALREADY IN HAND by the time this is asked for, so a material that will not read
    /// costs the monster its colour and not its picture. A grey monster beats no monster.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"textures":[{"filename":"a/b.dds" """)]
    [InlineData("""{"textures":"not an array"}""")]
    public void RubbishComesBackEmpty(string text)
    {
        MaterialFile said = MaterialFile.Parse(text);

        Assert.False(said.Ready);
        Assert.Equal(string.Empty, said.Albedo);
    }

    [Fact]
    public void NothingToReadIsNoneRatherThanAThrow()
    {
        Assert.False(MaterialFile.Read((byte[]?)null).Ready);
        Assert.False(MaterialFile.Read([]).Ready);
        Assert.False(MaterialFile.Read(null, "a/b.mat:0").Ready);
        Assert.False(MaterialFile.Parse(null).Ready);
    }
}
