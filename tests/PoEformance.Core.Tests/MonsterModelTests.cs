using System.Buffers.Binary;
using System.Text;
using PoEformance.Features;
using PoEformance.Game.Entities;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// The walk from a monster row to the triangles and the texture that clothe it.
/// </summary>
/// <remarks>
/// FIVE FILES AND FOUR FORMATS, and the interesting part is not any of the readers - each has its
/// own tests - but which file the walk asks for next. That is testable here without an install
/// because the walk reads through a function, so a dictionary stands in for the game's bundles.
///
/// THE INHERITED SKIN IS THE TEST THAT MATTERS. Of ten monsters checked against the game, nine
/// carry their own SkinMesh and BoneRabbleJaguar carries none at all - its skin is in the file it
/// extends. A walker that read only the monster's own .ao reports that one as having no model,
/// which looks like a gap in the game rather than a gap in the walk, and nine tenths of a sample
/// would never show it.
/// </remarks>
public class MonsterModelTests
{
    [Fact]
    public void AMonsterWithItsOwnSkinMeshIsFound()
    {
        MonsterModel said = MonsterModels.Of(Install().Read, Named("body.ao"));

        Assert.True(said.Ready);
        Assert.Equal(string.Empty, said.Why);
        Assert.Equal("art/rig.smd", said.Mesh_);
        Assert.Equal(2, said.Mesh.Triangles);
    }

    /// <summary>
    /// A monster whose own .ao has no SkinMesh takes the one from the file it extends.
    /// </summary>
    /// <remarks>
    /// THE REAL CASE THIS WAS WRITTEN FOR - BoneRabbleJaguar names only its own .ao, which carries
    /// three structs and no skin at all. Its model is eighteen thousand triangles behind two hops
    /// of extends, and without the walk it is a monster the tool says has no picture.
    /// </remarks>
    [Fact]
    public void AnInheritedSkinMeshIsFollowedThroughExtends()
    {
        var install = Install();
        install.Files["child.ao"] = Ao(extends: "body");

        MonsterModel said = MonsterModels.Of(install.Read, Named("child.ao"));

        Assert.True(said.Ready);
        Assert.Equal("art/rig.smd", said.Mesh_);
    }

    /// <summary>
    /// An extends line carries no extension, and the walk adds one.
    /// </summary>
    /// <remarks>
    /// THE GAME WRITES extends "Metadata/Parent" WITHOUT THE .ao while an attached object carries
    /// its own in full. The first survey over a real install asked for 3231 files and found 1687
    /// because of exactly this, so the rule is asserted rather than assumed.
    /// </remarks>
    [Fact]
    public void AnExtendsLineWithoutAnExtensionStillResolves()
    {
        var install = Install();
        install.Files["child.ao"] = Ao(extends: "body");

        // "body" has no extension; only "body.ao" is in the install.
        Assert.DoesNotContain("body", install.Files.Keys);
        Assert.True(MonsterModels.Of(install.Read, Named("child.ao")).Ready);
    }

    /// <summary>
    /// The nearer file's skin wins over the one it inherits.
    /// </summary>
    /// <remarks>
    /// ExpeditionBasicSkeleton CARRIES BOTH a skin and a remove_skin naming its base's. Walking
    /// outwards from the monster and taking the first found gets that right without reading
    /// remove_skin at all - and gets it wrong the moment the walk goes depth-first into the base
    /// before finishing the monster's own files.
    /// </remarks>
    [Fact]
    public void TheNearerFilesSkinWins()
    {
        var install = Install();
        install.Files["child.ao"] = Ao(extends: "body", skin: "art/other.sm");
        install.Files["art/other.sm"] = Sm("art/rig.smd", "art/other.mat");

        MonsterModel said = MonsterModels.Of(install.Read, Named("child.ao"));

        Assert.True(said.Ready);
        Assert.Equal("art/other.mat", said.Material);
    }

    /// <summary>
    /// The .ao's own material overrides the one the manifest names.
    /// </summary>
    /// <remarks>
    /// HOW ONE MESH SERVES SEVERAL MONSTERS. The manifest names a default and a monster's .ao
    /// overrides it per shape, with a ":n" selector after the file name - which is why one
    /// skeleton mesh appears as an expedition skeleton and as a bone rabble in different colours.
    ///
    /// REPORTED WITHOUT THE SELECTOR, which changed when several materials began to be tried:
    /// the number picks within the file and the file is what was read, so what is reported is
    /// the one that actually supplied the colour rather than the spelling it was named by.
    /// </remarks>
    [Fact]
    public void TheMonstersOwnMaterialBeatsTheManifests()
    {
        var install = Install();
        install.Files["body.ao"] = Ao(skin: "art/mesh.sm", material: "art/painted.mat:0");

        MonsterModel said = MonsterModels.Of(install.Read, Named("body.ao"));

        Assert.True(said.Ready);
        Assert.Equal("art/painted.mat", said.Material);
    }

    /// <summary>
    /// A monster built out of parts: the first material has no colour and the next one does.
    /// </summary>
    /// <remarks>
    /// REPORTED FROM THE LIVE CLIENT. Veynar the Frostbane draws in full and came out in plain
    /// ink, and he is plainly painted in the game - dark armour, red glow - so the colour map
    /// exists and the walk was not finding it. Taking the FIRST material the .ao names is what
    /// did that: a monster cut from one sheet names the same file per shape, which is what the
    /// old rule was measured on, and a monster built out of parts names a different one per
    /// shape - a cloth or a glow first, the body's skin second.
    ///
    /// The renderer puts one texture on the mesh, so the first material that actually carries a
    /// colour map is the one it gets. The shapes are ordered as the .ao lists them.
    /// </remarks>
    [Fact]
    public void AMaterialWithNoColourIsNotTheEndOfTheSearch()
    {
        var install = Install();

        // Two shapes, two different materials: a glow with only a normal map, then the skin.
        install.Files["body.ao"] = Ao(skin: "art/mesh.sm", material: "art/glow.mat:0", second: "art/painted.mat:1");
        install.Files["art/glow.mat"] = Mat("art/skin_normal.dds", slot: "NormalGloss_TEX");
        install.Files["art/skin.dds"] = Dds();

        MonsterModel said = MonsterModels.Of(install.Read, Named("body.ao"));

        Assert.True(said.Ready);
        Assert.True(said.Painted);
        Assert.Equal("art/painted.mat", said.Material);
        Assert.Equal(string.Empty, said.Paint);
    }

    /// <summary>
    /// Every shape gets the texture its own name was filed under.
    /// </summary>
    /// <remarks>
    /// THE SHAPE'S NAME IS THE JOIN, and it was being thrown away: the .ao's SkinMesh block holds
    /// one child per shape, keyed by that shape's name - <c>HipsShape = ".../Body.mat:0"</c> -
    /// and SkinnedMesh.Shapes carries the same names. Putting the two together is what lets the
    /// renderer paint a monster part by part instead of stretching one sheet over all of it.
    ///
    /// A NAME THAT MATCHES NOTHING FALLS BACK, which keeps every monster that names one material
    /// drawing exactly as it did - the change is confined to the ones that name several.
    /// </remarks>
    [Fact]
    public void AShapeWearsTheMaterialFiledUnderItsOwnName()
    {
        var install = Install();
        install.Files["art/skin.dds"] = Dds();
        install.Files["body.ao"] = Ao(skin: "art/mesh.sm", material: "art/painted.mat:0");

        MonsterModel said = MonsterModels.Of(install.Read, Named("body.ao"));

        // The fixture's mesh has one shape, called HipsShape, and the .ao files its material
        // under exactly that name.
        Assert.Equal("HipsShape", Assert.Single(said.Mesh.Shapes).Name);
        Assert.Single(said.Skins);
        Assert.Same(said.Skin, said.Skins[0]);
        Assert.Equal(["art/painted.mat"], said.Materials);

        // A material filed under a shape this mesh does not have leaves that shape on the
        // model's single skin rather than on nothing.
        var elsewhere = Install();
        elsewhere.Files["art/skin.dds"] = Dds();
        elsewhere.Files["body.ao"] = Ao(skin: "art/mesh.sm", material: "art/painted.mat:0", second: "art/other.mat:1");

        MonsterModel other = MonsterModels.Of(elsewhere.Read, Named("body.ao"));
        Assert.Single(other.Skins);
        Assert.NotNull(other.Skins[0]);
    }

    /// <summary>
    /// The number after a material picks the graph inside it, and so the shape's own texture.
    /// </summary>
    /// <remarks>
    /// THE OTHER HALF OF PAINTING A MONSTER PART BY PART, and the one that was hiding in plain
    /// sight: MaterialFile.Bare's own remark says the ":n" picks WITHIN the file, and nothing
    /// read it. A boss with one material and several shapes - which is most of them - had every
    /// shape reading the FIRST colour map in that file, so the head's sheet went on the cloak.
    /// Reported from the live client on Veynar, Connal and Count Geonor in the same evening.
    /// </remarks>
    [Fact]
    public void TheSelectorPicksWhichTextureTheShapeWears()
    {
        var install = Install();
        install.Files["art/second.dds"] = Dds();
        install.Files["art/paint.mat"] = Encoding.UTF8.GetBytes(
            """{"graphinstances":[{"custom_parameters":[{"name":"AlbedoTransparency_TEX","parameters":[{"path":"art/skin.dds"}]}]},{"custom_parameters":[{"name":"AlbedoTransparency_TEX","parameters":[{"path":"art/second.dds"}]}]}]}""");

        // The .sm names the material with no selector, so the model's own skin is the file's
        // first colour map - and the shape asks for the second graph by number.
        install.Files["art/skin.dds"] = Dds();
        install.Files["body.ao"] = Ao(skin: "art/mesh.sm", material: "art/paint.mat:1");

        MonsterModel said = MonsterModels.Of(install.Read, Named("body.ao"));

        Assert.True(said.Painted);
        Assert.Equal(["art/second.dds"], said.Textures);
        Assert.False(said.Guessed);
    }

    /// <summary>
    /// A colour map picked with nothing saying it is one is reported as a guess.
    /// </summary>
    /// <remarks>
    /// A mask or an occlusion map drawn as colour is a monster in greyscale, which reads as a
    /// missing texture and sends somebody looking for a file that is not lost - reported on the
    /// Vessel of Kulemak, who is painted from two sheets and looks unpainted. The pane says so
    /// now, and this is the flag it says it from.
    /// </remarks>
    [Fact]
    public void AColourMapNothingNamedIsMarkedAsAGuess()
    {
        var install = Install();
        install.Files["art/skin.dds"] = Dds();
        install.Files["art/paint.mat"] = Encoding.UTF8.GetBytes(
            """{"textures":[{"filename":"art/skin.dds","format":"DXT1"}]}""");

        MonsterModel said = MonsterModels.Of(install.Read, Named("body.ao"));

        Assert.True(said.Painted);
        Assert.Equal(["art/skin.dds"], said.Textures);
        Assert.True(said.Guessed);
    }

    /// <summary>And when none of them has one, the reason says all of them were asked.</summary>
    [Fact]
    public void EveryMaterialTriedIsNamedWhenNoneHasColour()
    {
        var install = Install();
        install.Files["body.ao"] = Ao(skin: "art/mesh.sm", material: "art/glow.mat:0", second: "art/rim.mat:1");
        install.Files["art/glow.mat"] = Mat("art/skin_normal.dds", slot: "NormalGloss_TEX");
        install.Files["art/rim.mat"] = Mat("art/rim_normal.dds", slot: "NormalGlossAO_TEX");

        string paint = MonsterModels.Of(install.Read, Named("body.ao")).Paint;

        // "none of the three had one" and "the one there is has none" are different findings,
        // and only the first says the shape-by-shape walk was tried and came back empty.
        Assert.Contains("none of the 3 materials", paint, StringComparison.Ordinal);
        Assert.Contains("glow.mat", paint, StringComparison.Ordinal);
        Assert.Contains("rim.mat", paint, StringComparison.Ordinal);
    }

    /// <summary>Every way the walk can stop says where it stopped.</summary>
    /// <remarks>
    /// A PICTURE THAT DOES NOT APPEAR IS THE SAME ON SCREEN whatever the reason, so the reason has
    /// to be carried out rather than swallowed - a monster with no .ao and a monster whose mesh
    /// file is missing are different problems with the same symptom.
    /// </remarks>
    [Fact]
    public void EveryWayOfFailingSaysWhy()
    {
        var install = Install();

        Assert.Contains("no install", MonsterModels.Of(null, Named("body.ao")).Why, StringComparison.Ordinal);
        Assert.Contains("names no .ao", MonsterModels.Of(install.Read, null).Why, StringComparison.Ordinal);
        Assert.Contains(
            "names no .ao",
            MonsterModels.Of(install.Read, new MonsterVariety(Name: "bare")).Why,
            StringComparison.Ordinal);

        Assert.Contains(
            "no SkinMesh",
            MonsterModels.Of(_ => null, Named("body.ao")).Why,
            StringComparison.Ordinal);

        var noMesh = Install();
        noMesh.Files.Remove("art/rig.smd");
        Assert.Contains(
            "did not read",
            MonsterModels.Of(noMesh.Read, Named("body.ao")).Why,
            StringComparison.Ordinal);

        var noManifest = Install();
        noManifest.Files.Remove("art/mesh.sm");
        Assert.Contains(
            "manifest did not read",
            MonsterModels.Of(noManifest.Read, Named("body.ao")).Why,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// What the walk read is added up file by file, and a file that was not there counts for nothing.
    /// </summary>
    /// <remarks>
    /// AGAINST A TALLY OF ITS OWN: the test wraps the fake install's read and adds up what it hands
    /// back, so the model's number is checked against what the walk actually asked for rather than
    /// against a sum somebody worked out by hand from the fixture - which would be the fixture
    /// checked against itself the moment the walk asked for one file more.
    /// </remarks>
    [Fact]
    public void WhatWasReadIsCounted()
    {
        var install = Install();
        var bytes = 0L;
        var files = 0;
        byte[]? Counting(string path)
        {
            byte[]? said = install.Read(path);
            if (said is not null)
            {
                bytes += said.Length;
                files++;
            }

            return said;
        }

        MonsterModel said = MonsterModels.Of(Counting, Named("body.ao"));

        Assert.True(said.Ready);
        Assert.Equal(bytes, said.Bytes);
        Assert.Equal(files, said.Files);
        Assert.True(said.Files >= 4, $"the chain is at least .ao, .sm, .smd and .mat, not {said.Files} files");
        Assert.True(said.Bytes > 0);

        // A texture that is not there is asked for and not counted: one file fewer, still a model.
        var bare = Install();
        bare.Files.Remove("art/skin.dds");
        MonsterModel unpainted = MonsterModels.Of(bare.Read, Named("body.ao"));
        Assert.True(unpainted.Ready);
        Assert.Equal(said.Files - 1, unpainted.Files);

        // And a walk that stops early still says what it read on the way there.
        var noManifest = Install();
        noManifest.Files.Remove("art/mesh.sm");
        MonsterModel stopped = MonsterModels.Of(noManifest.Read, Named("body.ao"));
        Assert.False(stopped.Ready);
        Assert.Equal(1, stopped.Files);
        Assert.Equal(install.Files["body.ao"].Length, stopped.Bytes);

        Assert.Equal(0, MonsterModel.None.Files);
        Assert.Equal(0L, MonsterModel.None.Bytes);
    }

    /// <summary>
    /// A file that extends itself does not hang the walk.
    /// </summary>
    /// <remarks>
    /// THE CALLER IS A WINDOW. A loop in the data has to cost a picture, not the tool - and the
    /// data is a game's, where nothing promises the graph is acyclic.
    /// </remarks>
    [Fact]
    public void ALoopInExtendsEnds()
    {
        var install = Install();
        install.Files["a.ao"] = Ao(extends: "b");
        install.Files["b.ao"] = Ao(extends: "a");

        MonsterModel said = MonsterModels.Of(install.Read, Named("a.ao"));

        Assert.False(said.Ready);
        Assert.Contains("no SkinMesh", said.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// A texture reached through a signpost is still found.
    /// </summary>
    /// <remarks>
    /// THE CASE A PLAIN READ LOSES. A texture in this game is one of three things and only the
    /// third is a .dds as it stands: a file whose content is a star and a path points at the one
    /// that really holds the picture, and another kind sits behind a compressed header. Decoding
    /// a bare read handles the third and silently fails the other two - a monster with a mesh and
    /// no colour, with nothing anywhere saying why.
    ///
    /// THE SAMPLE THIS WAS FIRST CHECKED AGAINST WAS A PLAIN .dds, which is exactly the case that
    /// proves nothing. GameArt has followed both hops since it was written for item icons; the
    /// monster walk was calling past it.
    /// </remarks>
    [Fact]
    public void ATextureBehindASignpostIsFollowed()
    {
        var install = Install();

        // The material's texture is now a signpost at the path the material names.
        install.Files["art/skin.dds"] = Encoding.ASCII.GetBytes("*art/real.dds");
        install.Files["art/real.dds"] = Dds();

        MonsterModel said = MonsterModels.Of(install.Read, Named("body.ao"));

        Assert.True(said.Ready);
        Assert.NotNull(said.Skin);
        Assert.True(said.Skin!.Top.Ready);
    }

    /// <summary>And a texture that is simply there is found the same way.</summary>
    [Fact]
    public void APlainTextureIsStillFound()
    {
        var install = Install();
        install.Files["art/skin.dds"] = Dds();

        MonsterModel said = MonsterModels.Of(install.Read, Named("body.ao"));

        Assert.True(said.Ready);
        Assert.NotNull(said.Skin);
    }

    /// <summary>The smallest uncompressed DDS the decoder accepts: 4x4, one colour.</summary>
    private static byte[] Dds()
    {
        var file = new byte[128 + (4 * 4 * 4)];
        "DDS "u8.CopyTo(file);

        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 124);          // header size
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), 0x0000100F);   // caps|height|width|pitch|pixelformat
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), 4);           // height
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), 4);           // width
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(20), 16);          // pitch

        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(76), 32);          // pixel format size
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(80), 0x41);        // rgb | alpha
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(88), 32);          // bits per pixel
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(92), 0x00FF0000);  // red
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(96), 0x0000FF00);  // green
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(100), 0x000000FF); // blue
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(104), 0xFF000000); // alpha
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(108), 0x1000);     // caps: texture

        Array.Fill(file, (byte)0xC0, 128, 4 * 4 * 4);
        return file;
    }

    /// <summary>A monster with no texture still has a mesh, and is drawn plain.</summary>
    [Fact]
    public void AMissingTextureCostsTheColourAndNotThePicture()
    {
        var install = Install();
        install.Files.Remove("art/skin.dds");

        MonsterModel said = MonsterModels.Of(install.Read, Named("body.ao"));

        Assert.True(said.Ready);
        Assert.Null(said.Skin);
        Assert.Equal(string.Empty, said.Why);
    }

    /// <summary>
    /// A monster row becomes pixels - the whole chain, end to end.
    /// </summary>
    /// <remarks>
    /// THE ONLY THING BETWEEN THIS AND THE SCREEN IS PLACEMENT. Every reader has its own tests and
    /// the renderer has its own, but nothing until here says the pieces fit: that what the walk
    /// hands back is what the renderer can draw. The two halves were written a day apart against
    /// different files, which is exactly when an interface drifts.
    /// </remarks>
    [Fact]
    public void AMonsterRowBecomesAPicture()
    {
        MonsterModel model = MonsterModels.Of(Install().Read, Named("body.ao"));
        Assert.True(model.Ready);

        GamePicture drawn = MeshPicture.Of(model.Mesh, 64, 0f, 0f, default, model.Skin);

        Assert.True(drawn.Ready);
        Assert.Equal(64, drawn.Width);
        Assert.Contains(drawn.Rgba, one => one != 0);
    }

    /// <summary>
    /// A monster drawn in plain ink says WHICH of the ways its colour went missing.
    /// </summary>
    /// <remarks>
    /// ASKED FROM THE LIVE CLIENT AND UNANSWERABLE FROM THE PICTURE, which is why this exists. A
    /// model with no texture on it is drawn in a pale warm grey that is all but indistinguishable
    /// from bare skin, so "is this monster missing its texture, or is it just pale" could only be
    /// settled by reading code. Every step of the walk already knew; not one of them said.
    ///
    /// THE LAST CASE IS THE ONE THAT IS NOT A FAILURE, and the reason the answers are separate
    /// strings rather than a flag. A mesh with no texture coordinates has a perfectly good texture
    /// and no way to look it up - the expected state of a bare body whose clothes are attached
    /// objects - and calling that "the file would not read" sends somebody hunting for a file that
    /// is not missing.
    /// </remarks>
    [Fact]
    public void PlainInkSaysWhichWayTheColourWentMissing()
    {
        var whole = Install();
        whole.Files["art/skin.dds"] = Dds();
        Assert.Equal(string.Empty, Paint(whole));
        Assert.True(MonsterModels.Of(whole.Read, Named("body.ao")).Painted);

        var noMaterial = Install();
        noMaterial.Files["art/mesh.sm"] = Encoding.UTF8.GetBytes(
            "version 6\nSkinnedMeshData \"art/rig.smd\"\nMaterials 0\nBoundingBox -1 -1 -1 1 1 1\n");
        Assert.Contains("names a material", Paint(noMaterial), StringComparison.Ordinal);

        var noMatFile = Install();
        noMatFile.Files.Remove("art/paint.mat");
        Assert.Contains("material did not read", Paint(noMatFile), StringComparison.Ordinal);

        // A material that reads perfectly well and carries only a normal map, in the game's own
        // normal-map slot: there is nothing here to paint a monster with, and drawing the normal
        // map instead would make it lilac - a shading bug to look at, hunted in the renderer.
        // The SLOT has to be wrong too, not just the file name: the slot outranks the name, so a
        // normal map filed under AlbedoTransparency_TEX is still the answer and this test passed
        // for the wrong reason until it said so.
        var noColour = Install();
        noColour.Files["art/paint.mat"] = Mat("art/skin_normal.dds", slot: "NormalGloss_TEX");
        Assert.Contains("no colour texture", Paint(noColour), StringComparison.Ordinal);

        // AND WHAT IT DOES NAME, which is the one reason here that is a question rather than an
        // answer: the rule for "which texture is the colour one" was measured on the materials
        // that happened to be looked at, so a boss whose slot is called something new is drawn
        // in plain ink and looks exactly like a boss with no texture at all. The material, its
        // slots and its textures are printed, which is what tells those two apart.
        string said = Paint(noColour);
        Assert.Contains("paint.mat", said, StringComparison.Ordinal);
        Assert.Contains("NormalGloss_TEX", said, StringComparison.Ordinal);
        Assert.Contains("skin_normal.dds", said, StringComparison.Ordinal);

        var noTexture = Install();
        noTexture.Files.Remove("art/skin.dds");
        Assert.Contains("texture did not read", Paint(noTexture), StringComparison.Ordinal);

        var rubbish = Install();
        rubbish.Files["art/skin.dds"] = Encoding.ASCII.GetBytes("not a texture at all");
        Assert.Contains("did not decode", Paint(rubbish), StringComparison.Ordinal);

        var unmapped = Install();
        unmapped.Files["art/skin.dds"] = Dds();
        unmapped.Files["art/rig.smd"] = Smd(coordinated: false);
        Assert.Contains("no texture coordinates", Paint(unmapped), StringComparison.Ordinal);

        // And it is NOT reported as a missing file: the texture was found, decoded and kept.
        MonsterModel bare = MonsterModels.Of(unmapped.Read, Named("body.ao"));
        Assert.NotNull(bare.Skin);
        Assert.False(bare.Painted);
        Assert.Equal(string.Empty, bare.Why);
    }

    private static string Paint(Fake install)
        => MonsterModels.Of(install.Read, Named("body.ao")).Paint;

    private static MonsterVariety Named(string path)
        => new(Name: "test", AoFiles: [path]);

    /// <summary>A stand-in install: the paths a walk may ask for, and their bytes.</summary>
    private sealed class Fake
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public byte[]? Read(string path) => Files.TryGetValue(path, out byte[]? said) ? said : null;
    }

    private static Fake Install()
    {
        var install = new Fake();
        install.Files["body.ao"] = Ao(skin: "art/mesh.sm");
        install.Files["art/mesh.sm"] = Sm("art/rig.smd", "art/paint.mat");
        install.Files["art/rig.smd"] = Smd();
        install.Files["art/paint.mat"] = Mat("art/skin.dds");
        install.Files["art/other.mat"] = Mat("art/skin.dds");
        install.Files["art/painted.mat"] = Mat("art/skin.dds");
        install.Files["art/skin.dds"] = [];
        return install;
    }

    /// <param name="second">
    /// A second shape's material, for the monsters built out of parts: the .ao names one child
    /// per shape and they are not always the same file.
    /// </param>
    private static byte[] Ao(
        string? extends = null, string? skin = null, string? material = null, string? second = null)
    {
        var said = new StringBuilder("version 3\n");
        if (extends is { Length: > 0 })
        {
            said.Append("extends \"").Append(extends).Append("\"\n");
        }

        if (skin is { Length: > 0 })
        {
            said.Append("SkinMesh\n{\n\tskin = \"").Append(skin).Append("\"\n");
            if (material is { Length: > 0 })
            {
                said.Append("\t\tHipsShape = \"").Append(material).Append("\"\n");
            }

            if (second is { Length: > 0 })
            {
                said.Append("\t\tCloakShape = \"").Append(second).Append("\"\n");
            }

            said.Append("}\n");
        }
        else
        {
            said.Append("AnimationController\n{\n\tblend = 1\n}\n");
        }

        return Encoding.UTF8.GetBytes(said.ToString());
    }

    private static byte[] Sm(string geometry, string material) => Encoding.UTF8.GetBytes(
        $"version 6\nSkinnedMeshData \"{geometry}\"\nMaterials 1\n\t\"{material}\" 1\n"
        + "BoundingBox -1 -1 -1 1 1 1\n");

    /// <param name="slot">
    /// The game's own name for what the texture is FOR. It decides, and outranks the file name -
    /// a normal map sitting in the albedo slot is still what the material says to paint with.
    /// </param>
    private static byte[] Mat(string texture, string slot = "AlbedoTransparency_TEX")
        => Encoding.UTF8.GetBytes(
            $$"""{"graphinstances":[{"custom_parameters":[{"name":"{{slot}}","parameters":[{"path":"{{texture}}"}]}]}]}""");

    /// <summary>The smallest mesh the reader accepts: two triangles over four vertices.</summary>
    /// <param name="coordinated">
    /// False writes every texture coordinate as zero, which is the shape a mesh with no
    /// coordinates really takes - the slots exist and hold nothing. The layout is unchanged, so
    /// this differs from a coordinated mesh in the one way that matters and in no other.
    /// </param>
    private static byte[] Smd(bool coordinated = true)
    {
        var file = new List<byte>();
        void U8(int v) => file.Add((byte)v);
        void U16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); file.AddRange(b); }
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); file.AddRange(b); }
        void F32(float v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, v); file.AddRange(b); }
        void F16(float v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, BitConverter.HalfToUInt16Bits((Half)v)); file.AddRange(b); }

        const string Name = "HipsShape";

        U8(3); U8(4); U16(1); U32((uint)(Name.Length * 2));
        F32(-1f); F32(1f); F32(-1f); F32(1f); F32(-1f); F32(1f);

        file.AddRange("DOLm"u8);
        U16(4); U8(1); U16(1); U32(0x23C);
        U32(2); U32(4);
        U32(0); U32(6);

        foreach (int one in new[] { 0, 1, 2, 1, 2, 3 })
        {
            U16(one);
        }

        foreach ((float x, float y, float z) in new[]
        {
            (-1f, -1f, -1f), (1f, 1f, 1f), (0f, 0f, 0f), (0.5f, 0.5f, 0.5f),
        })
        {
            F32(x); F32(y); F32(z);
            U8(0); U8(0); U8(127); U8(0);
            U8(127); U8(0); U8(0); U8(0);
            F16(coordinated ? 0.25f : 0f); F16(coordinated ? 0.75f : 0f);
            U8(1); U8(0); U8(0); U8(0);
            U8(255); U8(0); U8(0); U8(0);
        }

        U32(0);
        U32((uint)(Name.Length * 2));
        file.AddRange(Encoding.Unicode.GetBytes(Name));
        U32(4);
        for (var one = 0; one < 7; one++)
        {
            U32(0);
        }

        return [.. file];
    }
}
