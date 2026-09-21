using System.Numerics;
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

    /// <summary>
    /// A material list as long as the shape list is taken in shape order, name or no name.
    /// </summary>
    /// <remarks>
    /// COUNT GEONOR'S TWO FORMS SETTLED THIS. His wolf came back five shapes from five
    /// textures and looked right; his human came back fifteen shapes from ONE and was visibly
    /// in pieces, with the materials named and not matched - the .ao's keys and the mesh's
    /// shape names do not always agree. One entry per shape is a list in shape order, and
    /// using it is the same rule the mesh manifest's list already gets. Any other length stays
    /// unmatched: indexing into a list whose order is not established paints parts from
    /// whatever happens to line up, which is the failure this is fixing.
    /// </remarks>
    [Fact]
    public void MaterialsAreTakenInOrderWhenThereIsOnePerShape()
    {
        var install = Install();
        install.Files["art/skin.dds"] = Dds();
        install.Files["art/second.dds"] = Dds();

        // The fixture's mesh has one shape, called HipsShape. Name the material under a key
        // that shape does not have: matching by name finds nothing, and one entry for one
        // shape is still a list in shape order.
        install.Files["body.ao"] = Encoding.UTF8.GetBytes(
            "version 3\nSkinMesh\n{\n\tskin = \"art/mesh.sm\"\n\t\tSomeOtherShape = \"art/painted.mat\"\n}\n");
        install.Files["art/painted.mat"] = Mat("art/second.dds");

        MonsterModel said = MonsterModels.Of(install.Read, Named("body.ao"));

        Assert.True(said.Painted);
        Assert.Equal(["art/second.dds"], said.Textures);
        Assert.Equal(1, said.NamedInAo);
    }

    /// <summary>
    /// How many materials the files named, so "one sheet" can be told from "one sheet found".
    /// </summary>
    /// <remarks>
    /// A MONSTER OF THIRTY-FIVE SHAPES PAINTED FROM ONE TEXTURE is either right - one atlas for
    /// the whole body, which is ordinary - or a monster whose per-shape materials were not
    /// matched, and the picture is identical either way. Reported from the live client on Veynar
    /// and Connal, where the per-shape work changed nothing and nothing said whether it had
    /// anything to work with. The counts are what tells those two apart.
    /// </remarks>
    [Fact]
    public void HowManyMaterialsWereNamedIsCountedSeparatelyFromHowManyWereUsed()
    {
        var install = Install();
        install.Files["art/skin.dds"] = Dds();

        // One material in the .ao, one in the .sm, and the mesh's single shape wearing it.
        MonsterModel one = MonsterModels.Of(install.Read, Named("body.ao"));
        Assert.Equal(0, one.NamedInAo);
        Assert.Equal(1, one.NamedInMesh);
        Assert.Single(one.Materials);

        var two = Install();
        two.Files["art/skin.dds"] = Dds();
        two.Files["body.ao"] = Ao(skin: "art/mesh.sm", material: "art/painted.mat:0", second: "art/other.mat:1");

        MonsterModel said = MonsterModels.Of(two.Read, Named("body.ao"));
        Assert.Equal(2, said.NamedInAo);
        Assert.Equal(1, said.NamedInMesh);
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

    /// <summary>
    /// The pieces a monster hangs off itself are read and joined onto the body.
    /// </summary>
    /// <remarks>
    /// REPORTED FROM THE LIVE CLIENT: Doryani stands in the game in a skirt and the pane drew him
    /// bare-legged. His paint was never wrong - the geometry simply was not there, because a
    /// skirt is an attached_object with a mesh and a rig of its own and the body's files say
    /// nothing else about it.
    ///
    /// THE SOCKET IS THE HALF THAT BREAKS SILENTLY. The value is "hip_jntBnd &lt;path&gt;" inside one
    /// pair of quotes, so a walk that takes it whole asks for a path no install has and reports
    /// a monster with no attachments - which looks exactly like a monster that has none.
    /// </remarks>
    [Fact]
    public void AnAttachedPieceIsJoinedOntoTheBody()
    {
        var install = Install();
        install.Files["body.ao"] = Ao(skin: "art/mesh.sm", attach: "art/skirt.ao", skeleton: "art/rig.ast");
        install.Files["art/skirt.ao"] = Ao(skin: "art/skirt.sm");
        install.Files["art/skirt.sm"] = Sm("art/skirt.smd", "art/paint.mat");
        install.Files["art/skirt.smd"] = Smd();

        MonsterModel said = MonsterModels.Of(install.Read, Named("body.ao"));

        Assert.Equal(1, said.Parts);

        // The body is two triangles over four vertices; dressed it is twice that, and every
        // index still addresses a vertex - which an unshifted index would not.
        Assert.Equal(4, said.Mesh.Triangles);
        Assert.Equal(8, said.Mesh.Positions.Length);
        Assert.All(said.Mesh.Indices, one => Assert.InRange(one, 0, said.Mesh.Positions.Length - 1));

        // One texture per shape still holds across the join, which is what keeps the piece from
        // being painted out of the body's sheet.
        Assert.Equal(said.Mesh.Shapes.Count, said.Skins.Count);
    }

    /// <summary>With the pieces switched off the body comes back exactly as it did.</summary>
    /// <remarks>
    /// THE SWITCH EXISTS BECAUSE THE PIECES ARE NOT FREE, so the off position has to cost nothing
    /// as well as draw nothing: no attachment is read, and the mesh is the body's own.
    /// </remarks>
    [Fact]
    public void WithPartsOffTheBodyIsUnchanged()
    {
        var install = Install();
        install.Files["body.ao"] = Ao(skin: "art/mesh.sm", attach: "art/skirt.ao", skeleton: "art/rig.ast");
        install.Files["art/skirt.ao"] = Ao(skin: "art/skirt.sm");
        install.Files["art/skirt.sm"] = Sm("art/skirt.smd", "art/paint.mat");
        install.Files["art/skirt.smd"] = Smd();

        MonsterModel said = MonsterModels.Of(install.Read, Named("body.ao"), wearing: false);

        Assert.Equal(0, said.Parts);
        Assert.Equal(2, said.Mesh.Triangles);
    }

    /// <summary>
    /// A monster with no readable rig wears nothing, on purpose.
    /// </summary>
    /// <remarks>
    /// A PIECE IS MODELLED IN ITS OWN SPACE, so without the parent's rest pose there is nowhere
    /// to put it - measured on Doryani, whose thirteen pieces all have boxes a few tens of units
    /// across sitting on the origin. Joined anyway they pile up at his feet, which is a monster
    /// that looks broken rather than one that looks undressed. So the pieces are skipped and the
    /// body is drawn exactly as it always was.
    /// </remarks>
    [Fact]
    public void WithoutARigTheresNowhereToPutAPieceSoNoneIsWorn()
    {
        var install = Install();
        install.Files["body.ao"] = Ao(skin: "art/mesh.sm", attach: "art/skirt.ao");
        install.Files["art/skirt.ao"] = Ao(skin: "art/skirt.sm");
        install.Files["art/skirt.sm"] = Sm("art/skirt.smd", "art/paint.mat");
        install.Files["art/skirt.smd"] = Smd();

        MonsterModel said = MonsterModels.Of(install.Read, Named("body.ao"));

        Assert.True(said.Ready);
        Assert.Equal(0, said.Parts);
        Assert.Equal(2, said.Mesh.Triangles);
    }

    /// <summary>
    /// A piece socketed to "&lt;root&gt;" is already in the monster's space and is left there.
    /// </summary>
    /// <remarks>
    /// REPORTED FROM THE LIVE CLIENT. Bahlak the Sky Seer wears one piece and its line reads
    /// <c>socket &lt;root&gt;</c> - which is not a bone name at all, but how the game says "at my
    /// carrier's own origin". Read as an unknown bone it fell back to bone 0 and took the parent
    /// rig's ROOT transform, and the root carries the rig's own orientation: his feathers came
    /// out tipped over and laid flat on the floor at his feet.
    ///
    /// The piece is still WORN - it is the placement that was wrong - so this asserts it is
    /// there, which a rule that simply skipped unknown sockets would break.
    /// </remarks>
    [Fact]
    public void APieceAtTheRootIsWornAndNotPushedOntoABone()
    {
        var install = Install();
        install.Files["body.ao"] = Ao(
            skin: "art/mesh.sm", attach: "art/feathers.ao", socket: "<root>", skeleton: "art/rig.ast");
        install.Files["art/feathers.ao"] = Ao(skin: "art/feathers.sm");
        install.Files["art/feathers.sm"] = Sm("art/feathers.smd", "art/paint.mat");
        install.Files["art/feathers.smd"] = Smd();

        MonsterModel said = MonsterModels.Of(install.Read, Named("body.ao"));

        Assert.Equal(1, said.Parts);

        // Modelled at the origin like the body, and left there: no bone transform went on it.
        // The fixture mesh spans -1..1 on every axis, so a root transform with any rotation or
        // offset in it would show up as a box that is not the body's.
        Assert.Equal(said.Mesh.Least, said.Mesh.Most * -1f);
    }

    /// <summary>
    /// A top-level piece whose socket the body does not have is left out rather than dropped.
    /// </summary>
    /// <remarks>
    /// BONE 0 IS NOT AN ANSWER, IT IS THE FLOOR. Falling back to the root put a piece flat at the
    /// monster's feet, which reads as a bug in the renderer rather than a socket nobody could
    /// resolve. Nothing to place it with means it is not drawn - the same rule that leaves a
    /// monster with no rig undressed.
    /// </remarks>
    [Fact]
    public void ATopLevelPieceWithAnUnknownSocketIsLeftOut()
    {
        var install = Install();
        install.Files["body.ao"] = Ao(
            skin: "art/mesh.sm", attach: "art/tail.ao", socket: "no_such_jntBnd", skeleton: "art/rig.ast");
        install.Files["art/tail.ao"] = Ao(skin: "art/tail.sm");
        install.Files["art/tail.sm"] = Sm("art/tail.smd", "art/paint.mat");
        install.Files["art/tail.smd"] = Smd();

        MonsterModel said = MonsterModels.Of(install.Read, Named("body.ao"));

        Assert.True(said.Ready);
        Assert.Equal(0, said.Parts);
        Assert.Equal(2, said.Mesh.Triangles);
    }

    /// <summary>A piece whose files are missing leaves the body standing.</summary>
    /// <remarks>
    /// A MONSTER WITH ONE UNREADABLE ATTACHMENT IS STILL A MONSTER. The failure this guards
    /// against is a walk that answers "no model" for a boss because one dangler is not in the
    /// install - taking the body away over a piece of jewellery.
    /// </remarks>
    [Fact]
    public void APieceThatDoesNotReadDoesNotCostTheBody()
    {
        var install = Install();
        install.Files["body.ao"] = Ao(skin: "art/mesh.sm", attach: "art/missing.ao", skeleton: "art/rig.ast");

        MonsterModel said = MonsterModels.Of(install.Read, Named("body.ao"));

        Assert.True(said.Ready);
        Assert.Equal(0, said.Parts);
        Assert.Equal(2, said.Mesh.Triangles);
    }

    private static MonsterVariety Named(string path)
        => new(Name: "test", AoFiles: [path]);

    /// <summary>
    /// BasicSkeleton's rig, the same fixture AnimationSkeletonFromTheGameTests reads.
    /// </summary>
    /// <remarks>
    /// A REAL RIG AND NOT A BUILT ONE, because what the dressing needs from it is a rest pose in
    /// model space - and a hand-made skeleton would be testing the arithmetic against itself.
    /// This one carries root_jntBnd, hip_jntBnd and chest_jntBnd, which is exactly the shape a
    /// monster's attachments socket into.
    /// </remarks>
    private static byte[] Rig()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
        {
            dir = dir.Parent;
        }

        return File.ReadAllBytes(
            Path.Combine(dir!.FullName, "tests", "fixtures", "basicskeleton-rig.headers.ast"));
    }

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
        install.Files["art/rig.ast"] = Rig();
        return install;
    }

    /// <param name="second">
    /// A second shape's material, for the monsters built out of parts: the .ao names one child
    /// per shape and they are not always the same file.
    /// </param>
    /// <param name="attach">
    /// A piece hung off this one, written the way the game does: a SOCKET and then a path, both
    /// inside one pair of quotes. Taken whole that is a path no install has, which is exactly
    /// the trap the walk has to get past.
    /// </param>
    private static byte[] Ao(
        string? extends = null,
        string? skin = null,
        string? material = null,
        string? second = null,
        string? attach = null,
        string socket = "hip_jntBnd",
        string? skeleton = null,
        string? fixture = null,
        string? also = null)
    {
        var said = new StringBuilder("version 3\n");
        if (fixture is { Length: > 0 })
        {
            said.Append("client\n{\n\tFixedMesh\n\t{\n\t\tfixed_mesh = \"")
                .Append(fixture).Append("\"\n\t}\n}\n");
        }

        if (extends is { Length: > 0 })
        {
            said.Append("extends \"").Append(extends).Append("\"\n");
        }

        if (skeleton is { Length: > 0 })
        {
            said.Append("client\n{\n\tClientAnimationController\n\t{\n\t\tskeleton = \"")
                .Append(skeleton).Append("\"\n\t}\n}\n");
        }

        if (attach is { Length: > 0 })
        {
            said.Append("AttachedAnimatedObject\n{\n\tattached_object = \"")
                .Append(socket).Append(' ').Append(attach).Append("\"\n}\n");
        }

        if (skin is { Length: > 0 })
        {
            said.Append("SkinMesh\n{\n\tskin = \"").Append(skin).Append("\"\n");
            if (also is { Length: > 0 })
            {
                said.Append("\tskin = \"").Append(also).Append("\"\n");
            }

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

    /// <summary>
    /// A piece rigged to the parent's OWN bones is skinned to them, not pinned to one of them.
    /// </summary>
    /// <remarks>
    /// THE BUG THIS PINS DOWN was invisible at rest and obvious the moment anything moved. Every
    /// attached piece used to have all four of every vertex's bone slots overwritten with ONE
    /// bone - the socket's - which for a piece socketed "&lt;root&gt;" is the rig root: the one
    /// bone that does not follow the body. So Bahlak the Sky Seer's feather bundle stayed on the
    /// floor while he rose, and Malgor the Nautilord's seaweed hung in the air behind him, and a
    /// piece socketed to an arm looked perfect the whole time.
    ///
    /// MEASURED, NOT ASSUMED: the seaweed's own rig is 23 bones of which TWELVE carry the
    /// parent's names and rest where the parent's rest, and the feathers' is 31 with eleven. A
    /// prop shares only root_jntBnd. Here the piece is given the parent's own rig, so every bone
    /// matches and the piece's own binding has to survive the join untouched.
    /// </remarks>
    [Fact]
    public void APieceRiggedToTheParentsOwnBonesIsSkinnedToThemRatherThanPinnedToOne()
    {
        var install = Install();
        install.Files["body.ao"] = Ao(
            skin: "art/mesh.sm", attach: "art/cloak.ao", socket: "<root>", skeleton: "art/rig.ast");
        install.Files["art/cloak.ao"] = Ao(skin: "art/cloak.sm", skeleton: "art/rig.ast");
        install.Files["art/cloak.sm"] = Sm("art/cloak.smd", "art/paint.mat");
        install.Files["art/cloak.smd"] = Smd();

        MonsterModel said = MonsterModels.Of(install.Read, Named("body.ao"));

        Assert.Equal(1, said.Parts);

        // The fixture binds every vertex to bone 1 alone. The piece's four vertices start at 4,
        // so its first vertex's four slots are at 16 - and they still say what its file said.
        Assert.Equal<byte>([1, 0, 0, 0], said.Mesh.Bones[16..20]);
        Assert.Equal<byte>([255, 0, 0, 0], said.Mesh.Weights[16..20]);

        // And it was not moved: a piece in the monster's space already needs no transform, so
        // its vertices sit exactly where the body's do.
        Assert.Equal(said.Mesh.Positions[0], said.Mesh.Positions[4]);
    }

    /// <summary>
    /// A bone's parent can carry a HIGHER number than the bone, and the walk has to survive it.
    /// </summary>
    /// <remarks>
    /// THE ASSUMPTION THAT COST A SECOND ROUND. Matching a piece's bones onto the parent's walks
    /// up the piece's own tree looking for a name the parent has, and the first version read the
    /// parent's answer straight out of the array - on the reasoning that a parent is always
    /// earlier in the list. It is not: <see cref="SkeletonPose"/> builds the tree out of the
    /// file's child-and-sibling links with a stack, and the numbers are the file's to choose.
    /// Bahlak the Sky Seer's feathers are exactly that - phys_skinned_head_feathers_1 is bone 19
    /// and hangs off M_head_jntBnd, bone 22.
    ///
    /// WHAT IT LOOKED LIKE: every bone that lost that race fell back to the rig root, which is
    /// the one bone that does not follow the body, so the piece came out stretched between the
    /// monster and the origin - a long black spike from his chest down to the floor. The rig
    /// here is the smallest one that reproduces it: root, then a chest whose own child is the
    /// bone numbered below it.
    /// </remarks>
    [Fact]
    public void APieceWhoseBonesOutnumberTheirParentsStillFindsThemByName()
    {
        // The piece's rig: root(0) -> chest(2) -> feather(1). The feather's parent is NUMBERED
        // above it, which is the whole point of the fixture.
        byte[] piece = Packed.Skeleton(
            [
                ("root_jntBnd", 255, 2, 0f),
                ("phys_feather_jntBnd", 255, 255, -100f),
                ("chest_jntBnd", 255, 1, -90f),
            ],
            []);

        var install = Install();
        install.Files["art/rig.ast"] = Packed.Skeleton(
            [("root_jntBnd", 255, 1, 0f), ("chest_jntBnd", 255, 255, -90f)], []);
        install.Files["body.ao"] = Ao(
            skin: "art/mesh.sm", attach: "art/cloak.ao", socket: "<root>", skeleton: "art/rig.ast");
        install.Files["art/cloak.ao"] = Ao(skin: "art/cloak.sm", skeleton: "art/cloak.ast");
        install.Files["art/cloak.ast"] = piece;
        install.Files["art/cloak.sm"] = Sm("art/cloak.smd", "art/paint.mat");
        install.Files["art/cloak.smd"] = Smd();

        MonsterModel said = MonsterModels.Of(install.Read, Named("body.ao"));

        Assert.Equal(1, said.Parts);

        // The fixture binds every vertex to the piece's bone 1 - the feather, whose name the
        // parent does not have. Walking up reaches chest_jntBnd, which the parent has at 1. A
        // walk that gave up would say 0, and 0 is the root the piece was stranded on.
        Assert.Equal<byte>([1, 0, 0, 0], said.Mesh.Bones[16..20]);
    }

    /// <summary>
    /// A piece that NAMES a socket is placed there, whatever its own rig happens to share.
    /// </summary>
    /// <remarks>
    /// THE ATTACHMENT LINE CARRIES THE ANSWER, and two goes at reading the piece's rig instead
    /// each broke a monster the other fixed. Counting shared bone names turned the Frostborn
    /// Fiend's block of ice - three bones, of which the parent rig has two - into a skinned cloak
    /// and stood it upright on the floor beside him. Requiring the shared bones to rest in the
    /// same place as the parent's then rejected Bahlak's feathers, which are the clearest skinned
    /// piece there is, and dropped them on the floor in three flat clumps.
    ///
    /// A LINE THAT NAMES A BONE IS PLACING THE PIECE THERE - the anchor at aux_anchor_jntBnd, the
    /// beard at head_jntBnd, the ice at R_Weapon. This fixture is the hardest version of that:
    /// the piece's rig is the PARENT'S OWN, every name shared and every bone resting in the same
    /// place, and it must still go to its socket because that is what the line says.
    /// </remarks>
    [Fact]
    public void APieceThatNamesASocketIsPlacedThereWhateverItsRigShares()
    {
        byte[] rig = Packed.Skeleton(
            [("root_jntBnd", 255, 1, 0f), ("chest_jntBnd", 255, 255, -60f)], []);

        var install = Install();
        install.Files["art/rig.ast"] = rig;
        install.Files["body.ao"] = Ao(
            skin: "art/mesh.sm", attach: "art/ice.ao", socket: "chest_jntBnd", skeleton: "art/rig.ast");
        install.Files["art/ice.ao"] = Ao(skin: "art/ice.sm", skeleton: "art/rig.ast");
        install.Files["art/ice.sm"] = Sm("art/ice.smd", "art/paint.mat");
        install.Files["art/ice.smd"] = Smd();

        MonsterModel said = MonsterModels.Of(install.Read, Named("body.ao"));

        Assert.Equal(1, said.Parts);

        // Rigid at the socket: every slot is that one bone, and the piece was moved to where it
        // rests - sixty down - rather than left in its own space at the origin.
        Assert.All(said.Mesh.Bones[16..20], one => Assert.Equal<byte>(1, one));
        Assert.Equal(-60f, said.Mesh.Positions[4].Z - said.Mesh.Positions[0].Z, 3);
    }

    /// <summary>
    /// A socketed piece's rig is the parent's subtree, and its vertices follow the bone they name.
    /// </summary>
    /// <remarks>
    /// MEASURED, AND EXACTLY. The Fallen Knight's bicep fingers are socketed at
    /// L_shoulder_jntBnd, and their own rig carries the parent's next three bones with the SOCKET
    /// at its origin:
    ///
    ///     1 L_shoulder_jntBnd        rests at (0,0,0)       parent has (22.3,7,-157.9)
    ///     2 L_shoulder_cTwist_jntBnd rests at (13.4,0,8.7)  parent has (35.7,7,-149.2)
    ///
    /// The differences are the same numbers. So a socketed piece is not a rigid prop at all: it
    /// is the parent's subtree re-rooted on the socket, and binding every one of its vertices to
    /// the socket alone means the half of it that belongs to an elbow does not bend with one.
    ///
    /// AT REST IT LOOKS IDENTICAL, which is why it lasted: a correct bone assignment and a wrong
    /// one agree exactly while the pose is the bind pose, and differ the moment anything moves.
    ///
    /// THE PIECE'S OWN ROOT IS THE SOCKET, whatever it is called - it rests at the piece's origin
    /// and matching it by name would send those vertices to the monster's own root instead.
    /// </remarks>
    [Fact]
    public void ASocketedPiecesVerticesFollowTheBoneTheyNameAndNotJustItsSocket()
    {
        var install = Install();
        install.Files["art/rig.ast"] = Packed.Skeleton(
            [
                ("root_jntBnd", 255, 1, 0f),
                ("L_shoulder_jntBnd", 255, 2, -150f),
                ("L_elbow_jntBnd", 255, 255, -40f),
            ],
            []);

        // The piece's rig: the same subtree, with the socket at its own origin.
        install.Files["art/hand.ast"] = Packed.Skeleton(
            [
                ("root_jntBnd", 255, 1, 0f),
                ("L_shoulder_jntBnd", 255, 2, 0f),
                ("L_elbow_jntBnd", 255, 255, -40f),
            ],
            []);

        install.Files["body.ao"] = Ao(
            skin: "art/mesh.sm", attach: "art/hand.ao", socket: "L_shoulder_jntBnd", skeleton: "art/rig.ast");
        install.Files["art/hand.ao"] = Ao(skin: "art/hand.sm", skeleton: "art/hand.ast");
        install.Files["art/hand.sm"] = Sm("art/hand.smd", "art/paint.mat");

        // Every vertex weighted to the piece's ELBOW, which is not the socket.
        install.Files["art/hand.smd"] = Packed.Mesh(
        [
            .. new[] { 0f, 1f, 2f, 3f }.Select(one =>
                new Packed.Vertex(new Vector3(one, 0f, 0f), [2, 0, 0, 0], [255, 0, 0, 0])),
        ]);

        MonsterModel said = MonsterModels.Of(install.Read, Named("body.ao"));

        Assert.Equal(1, said.Parts);

        // Bone 2 of the piece is bone 2 of the parent - the elbow - and not the socket at 1.
        Assert.Equal<byte>(2, said.Mesh.Bones[16]);

        // And it still sits at its socket: the piece's own vertices are at z nought in its own
        // space, so the socket's rest transform is the whole of where they end up.
        Assert.Equal(-150f, said.Mesh.Positions[4].Z, 3);
    }

    /// <summary>
    /// A piece that names no socket and shares no bone but the root stays where it is.
    /// </summary>
    /// <remarks>
    /// THE ONE CASE STILL NOT SETTLED, pinned so it cannot change by accident. Malgor the
    /// Nautilord's ship's wheel is socketed "&lt;root&gt;" - so nothing places it - and its rig is
    /// ten bones of which the parent shares only root_jntBnd, so there is nothing to move it onto
    /// either. It lands at the monster's origin. Whether that is where the game puts it is a
    /// question for a picture; what this fixes is that nothing here invents an answer.
    /// </remarks>
    [Fact]
    public void APieceWithNoSocketAndNoSharedBonesIsLeftAtTheOrigin()
    {
        byte[] piece = Packed.Skeleton(
            [("root_jntBnd", 255, 1, 0f), ("wheel_jntBnd", 255, 255, -40f)], []);

        var install = Install();
        install.Files["art/rig.ast"] = Packed.Skeleton(
            [("root_jntBnd", 255, 1, 0f), ("chest_jntBnd", 255, 255, -60f)], []);
        install.Files["body.ao"] = Ao(
            skin: "art/mesh.sm", attach: "art/wheel.ao", socket: "<root>", skeleton: "art/rig.ast");
        install.Files["art/wheel.ao"] = Ao(skin: "art/wheel.sm", skeleton: "art/wheel.ast");
        install.Files["art/wheel.ast"] = piece;
        install.Files["art/wheel.sm"] = Sm("art/wheel.smd", "art/paint.mat");
        install.Files["art/wheel.smd"] = Smd();

        MonsterModel said = MonsterModels.Of(install.Read, Named("body.ao"));

        Assert.Equal(1, said.Parts);
        Assert.All(said.Mesh.Bones[16..20], one => Assert.Equal<byte>(0, one));
        Assert.Equal(said.Mesh.Positions[0], said.Mesh.Positions[4]);
    }

    /// <summary>
    /// The turn and the shift an attachment line asks for are applied on top of its socket.
    /// </summary>
    /// <remarks>
    /// TWO LINES THAT WERE BEING THROWN AWAY. An attached_object entry can carry children, and
    /// the Frostborn Fiend's block of ice carries <c>attached_object_translation = "0 0 -55"</c>
    /// and <c>attached_object_rotation = "-3.141 -0 0"</c> - half a turn about x and a shift of
    /// fifty-five. Without them the ice he is holding stands upright on the floor beside him.
    /// </remarks>
    [Fact]
    public void AnAttachmentsOwnTurnAndShiftRideOnTopOfItsSocket()
    {
        var install = Install();
        install.Files["art/rig.ast"] = Packed.Skeleton(
            [("root_jntBnd", 255, 1, 0f), ("hip_jntBnd", 255, 255, 0f)], []);
        install.Files["body.ao"] = Encoding.UTF8.GetBytes(
            "version 3\n"
            + "client\n{\n\tClientAnimationController\n\t{\n\t\tskeleton = \"art/rig.ast\"\n\t}\n}\n"
            + "AttachedAnimatedObject\n{\n"
            + "\tattached_object = \"hip_jntBnd art/ice.ao\"\n"
            + "\t\tattached_object_translation = \"0 0 -55\"\n"
            + "}\n"
            + "SkinMesh\n{\n\tskin = \"art/mesh.sm\"\n}\n");
        install.Files["art/ice.ao"] = Ao(skin: "art/ice.sm");
        install.Files["art/ice.sm"] = Sm("art/ice.smd", "art/paint.mat");
        install.Files["art/ice.smd"] = Smd();

        MonsterModel said = MonsterModels.Of(install.Read, Named("body.ao"));

        Assert.Equal(1, said.Parts);

        // The socket rests at the origin, so the whole of the offset is the line's own shift.
        Assert.Equal(-55f, said.Mesh.Positions[4].Z - said.Mesh.Positions[0].Z, 3);
    }

    /// <summary>
    /// A prop that shares nothing but the root is still pinned rigidly to its socket.
    /// </summary>
    /// <remarks>
    /// THE OTHER HALF, AND THE ONE THAT WAS ALWAYS RIGHT. Malgor's anchor, beard and ship's wheel
    /// each bring a rig of their own whose only shared name is root_jntBnd - which every rig in
    /// the game has, so matching it means nothing. Each is modelled around its own origin and
    /// belongs rigidly at its socket, and the fix above must not take that away from them.
    /// </remarks>
    [Fact]
    public void APropSharingOnlyTheRootIsStillPinnedToItsSocket()
    {
        var install = Install();
        install.Files["body.ao"] = Ao(
            skin: "art/mesh.sm", attach: "art/horn.ao", socket: "chest_jntBnd", skeleton: "art/rig.ast");
        install.Files["art/horn.ao"] = Ao(skin: "art/horn.sm");
        install.Files["art/horn.sm"] = Sm("art/horn.smd", "art/paint.mat");
        install.Files["art/horn.smd"] = Smd();

        MonsterModel said = MonsterModels.Of(install.Read, Named("body.ao"));

        Assert.Equal(1, said.Parts);

        // Every slot is the socket's bone, which is what makes a prop follow it and nothing else.
        Assert.All(said.Mesh.Bones[16..20], one => Assert.Equal(said.Mesh.Bones[16], one));
        Assert.NotEqual(0, said.Mesh.Bones[16]);
    }

    /// <summary>
    /// A SkinMesh block naming several skins is a body in several files, and all of them are read.
    /// </summary>
    /// <remarks>
    /// READ AS A FIELD FOR AS LONG AS IT EXISTED, and it is a list. Reported from the live client:
    /// Zar Wali, the Bone Tyrant is a giant snake skeleton and the pane drew two floating arms -
    /// his .ao names seven manifests in seven consecutive lines, GSSBArmA through GSSBTail, and
    /// this walk took the first and stopped. The sections share one rig, so nothing is remapped
    /// and nothing is placed; they are simply all there.
    /// </remarks>
    [Fact]
    public void EverySkinASkinMeshBlockNamesIsReadAndNotJustTheFirst()
    {
        var install = Install();
        install.Files["body.ao"] = Ao(skin: "art/mesh.sm", also: "art/tail.sm", skeleton: "art/rig.ast");
        install.Files["art/tail.sm"] = Sm("art/tail.smd", "art/paint.mat");
        install.Files["art/tail.smd"] = Smd();

        MonsterModel said = MonsterModels.Of(install.Read, Named("body.ao"));

        Assert.Equal(2, said.Sections);
        Assert.Equal(4, said.Mesh.Triangles);
        Assert.Equal(8, said.Mesh.Positions.Length);
        Assert.All(said.Mesh.Indices, one => Assert.InRange(one, 0, said.Mesh.Positions.Length - 1));
        Assert.Equal(said.Mesh.Shapes.Count, said.Skins.Count);
    }

    /// <summary>A section is not an accessory: switching the parts off must not behead a monster.</summary>
    [Fact]
    public void TheBodysOtherSectionsSurviveThePartsBeingSwitchedOff()
    {
        var install = Install();
        install.Files["body.ao"] = Ao(skin: "art/mesh.sm", also: "art/tail.sm", skeleton: "art/rig.ast");
        install.Files["art/tail.sm"] = Sm("art/tail.smd", "art/paint.mat");
        install.Files["art/tail.smd"] = Smd();

        MonsterModel said = MonsterModels.Of(install.Read, Named("body.ao"), wearing: false);

        Assert.Equal(0, said.Parts);
        Assert.Equal(4, said.Mesh.Triangles);
    }

    /// <summary>
    /// A piece that is a rigid prop - a FixedMesh naming a <c>.fmt</c> - is joined on too.
    /// </summary>
    /// <remarks>
    /// THE HALF OF THE ATTACHMENTS THE WALK USED TO THROW AWAY. An .ao hangs either a SkinMesh or
    /// a FixedMesh, and asking only for the first meant every rigid prop in the game - Malgor,
    /// the Nautilord's cannon among them, reported missing from the live client - came back as
    /// "no SkinMesh" and was skipped. A .fmt brings no .sm and no rig, and it carries its own
    /// material per shape, so the whole path underneath it is different and only the join is
    /// shared. Counting the vertices is what says it really arrived.
    /// </remarks>
    [Fact]
    public void APropIsJoinedOnEvenThoughItHasNoSkinAndNoManifest()
    {
        var install = Install();
        install.Files["body.ao"] = Ao(skin: "art/mesh.sm", attach: "art/cannon.ao", skeleton: "art/rig.ast");
        install.Files["art/cannon.ao"] = Ao(fixture: "art/cannon.fmt");
        install.Files["art/cannon.fmt"] = Packed.Fmt("art/painted.mat");

        MonsterModel said = MonsterModels.Of(install.Read, Named("body.ao"));

        Assert.Equal(1, said.Parts);
        Assert.Equal(4, said.Mesh.Triangles);
        Assert.Equal(8, said.Mesh.Positions.Length);
        Assert.All(said.Mesh.Indices, one => Assert.InRange(one, 0, said.Mesh.Positions.Length - 1));

        // The prop's own shape name came out of its own string pool and rode the join.
        Assert.Contains("BarrelShape", said.Mesh.Shapes.Select(one => one.Name), StringComparer.Ordinal);
        Assert.Equal(said.Mesh.Shapes.Count, said.Skins.Count);
    }

    /// <summary>A prop switched off with the rest of the parts, like any other piece.</summary>
    [Fact]
    public void APropIsLeftOutWhenThePartsAreSwitchedOff()
    {
        var install = Install();
        install.Files["body.ao"] = Ao(skin: "art/mesh.sm", attach: "art/cannon.ao", skeleton: "art/rig.ast");
        install.Files["art/cannon.ao"] = Ao(fixture: "art/cannon.fmt");
        install.Files["art/cannon.fmt"] = Packed.Fmt("art/painted.mat");

        MonsterModel said = MonsterModels.Of(install.Read, Named("body.ao"), wearing: false);

        Assert.Equal(0, said.Parts);
        Assert.Equal(2, said.Mesh.Triangles);
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
