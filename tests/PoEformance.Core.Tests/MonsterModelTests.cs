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
    /// </remarks>
    [Fact]
    public void TheMonstersOwnMaterialBeatsTheManifests()
    {
        var install = Install();
        install.Files["body.ao"] = Ao(skin: "art/mesh.sm", material: "art/painted.mat:0");

        MonsterModel said = MonsterModels.Of(install.Read, Named("body.ao"));

        Assert.True(said.Ready);
        Assert.Equal("art/painted.mat:0", said.Material);
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

    private static byte[] Ao(string? extends = null, string? skin = null, string? material = null)
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

    private static byte[] Mat(string texture) => Encoding.UTF8.GetBytes(
        $$"""{"graphinstances":[{"custom_parameters":[{"name":"AlbedoTransparency_TEX","parameters":[{"path":"{{texture}}"}]}]}]}""");

    /// <summary>The smallest mesh the reader accepts: two triangles over four vertices.</summary>
    private static byte[] Smd()
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
            F16(0.25f); F16(0.75f);
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
