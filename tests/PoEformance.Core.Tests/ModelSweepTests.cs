using System.Numerics;
using System.Text;
using PoEformance.Features;
using PoEformance.Game.Entities;
using Xunit;

namespace PoEformance.Core.Tests;

/// <summary>
/// The sweep over the whole monster table: that it walks it, counts it, and flags the right ones.
/// </summary>
/// <remarks>
/// WHAT THESE ARE FOR. The sweep's whole value is finding the monsters nobody has looked at, so
/// the one thing it must not do is stay quiet about them. Each test here builds the monster that
/// cost a day - the piece with no bone in common, the piece hung off a piece, the merged rig, the
/// attachment_bones line that names no group - and asserts the flag it earns.
///
/// AND THAT IT READS NO PICTURES, which is what makes a run over 2733 monsters finish. That is an
/// invariant of the reader the sweep hands the walk, so it is asserted rather than assumed.
/// </remarks>
public sealed class ModelSweepTests
{
    /// <summary>
    /// A monster whose pieces all find their bones is walked and flagged for nothing.
    /// </summary>
    /// <remarks>
    /// THE CASE THAT MUST STAY QUIET. A sweep that flags everything is a sweep nobody reads, so
    /// the ordinary monster - a piece socketed to a bone the rig has, whose own rig shares that
    /// bone - is the baseline the others are measured against.
    /// </remarks>
    [Fact]
    public void AMonsterWhosePiecesAllFindTheirBonesEarnsNoFlags()
    {
        Fake install = Dressed();

        SweepResult said = Swept(install, "quiet.ao");

        Assert.Equal(1, said.Tally.Walked);
        Assert.Equal(1, said.Tally.Pieces);
        Assert.Equal(0, said.Tally.Odd);
        Assert.Empty(said.Flagged);
    }

    /// <summary>
    /// A "&lt;root&gt;" piece with a rig of its own and no bone in common is flagged unfitted.
    /// </summary>
    /// <remarks>
    /// MALGOR THE NAUTILORD'S SHIP'S WHEEL, which is the one case still not settled: socketed
    /// "&lt;root&gt;", so nothing places it, and ten bones of which the parent shares only the
    /// root, so there is nothing to move it onto either. It lands at the monster's origin. The
    /// sweep cannot say whether that is where the game puts it - it can say that this is a
    /// monster somebody should look at, which is all a flag is for.
    /// </remarks>
    [Fact]
    public void APieceWithARigAndNoBoneInCommonIsFlaggedUnfitted()
    {
        Fake install = Dressed();
        install.Files["art/wheel.ast"] = Packed.Skeleton(
            [("root_jntBnd", 255, 1, 0f), ("wheel_jntBnd", 255, 255, -40f)], []);
        install.Files["wheel.ao"] = Ao(
            skin: "art/mesh.sm", attach: "art/wheel.ao", socket: "<root>", skeleton: "art/rig.ast");
        install.Files["art/wheel.ao"] = Ao(skin: "art/wheel.sm", skeleton: "art/wheel.ast");
        install.Files["art/wheel.sm"] = Sm("art/wheel.smd");
        install.Files["art/wheel.smd"] = Packed.Mesh(Vertices(1));

        SweepResult said = Swept(install, "wheel.ao");

        Assert.Contains("unfitted", said.Tally.Flags.Keys);
        Assert.Single(said.Flagged);
    }

    /// <summary>
    /// A piece part of which found bones and part of which did not is flagged torn.
    /// </summary>
    /// <remarks>
    /// BAHLAK'S FEATHER BUNDLE, and the reason this flag counts what it counts. A piece's own rig
    /// ROOT never matches by design - a socketed piece's root IS its socket, whatever it is
    /// called - so "some bone of this piece matched nothing" is true of very nearly every piece
    /// there is, and a flag on that would fire on the whole table and mean nothing.
    ///
    /// What is a symptom is the MIXTURE: part of the piece corrected onto the monster and part of
    /// it left on his root, which is the floor, so the piece comes out stretched between the two.
    /// Here the piece's second bone is a spine the body has and its third hangs off the root and
    /// is named nothing the body knows.
    /// </remarks>
    [Fact]
    public void APiecePartOfWhichFoundBonesAndPartOfWhichDidNotIsFlaggedTorn()
    {
        Fake install = Dressed();
        install.Files["torn.ao"] = Ao(
            skin: "art/mesh.sm", attach: "art/cloak.ao", socket: "<root>", skeleton: "art/rig.ast");

        // Bone 1 is the body's hip; bone 2 hangs off the root under a name it has never heard of.
        install.Files["art/cloak.ast"] = Packed.Skeleton(
            [
                ("root_jntBnd", 255, 1, 0f),
                ("hip_jntBnd", 2, 255, -150f),
                ("phys_skinned_feather_jntBnd", 255, 255, -20f),
            ],
            []);

        SweepResult said = Swept(install, "torn.ao");

        Assert.Contains("torn", said.Tally.Flags.Keys);
        Assert.Contains("\"strays\":1", Lines(install, "torn.ao")[0]);

        // And the baseline monster, whose piece matches everything but its own root, does not.
        Assert.DoesNotContain("torn", Swept(Dressed(), "quiet.ao").Tally.Flags.Keys);
    }

    /// <summary>
    /// A piece whose box does not meet the monster's own is flagged apart.
    /// </summary>
    /// <remarks>
    /// THE TEST THAT WAS MISSING, AND THE SWEEP IS WHAT NOTICED. The first version of this flag
    /// held each piece up against <c>model.Mesh</c>'s box - the JOINED mesh, which already has
    /// every piece in it. A piece is inside that box by construction, so the check could not
    /// fail, and over 2792 real monsters it flagged not one. A check a wrong value passes is
    /// worse than no check; a check NO value can fail is worse again, because the silence reads
    /// as good news.
    ///
    /// So the reference is the monster WITHOUT what he is wearing, and this asserts both halves:
    /// a piece parked a thousand units from him is named, and the one sitting on him is not.
    /// </remarks>
    [Fact]
    public void APieceNowhereNearTheMonsterIsFlaggedApart()
    {
        Fake install = Dressed();

        // A socket a thousand down, which is nowhere near a body modelled around the origin.
        install.Files["art/rig.ast"] = Packed.Skeleton(
            [("root_jntBnd", 255, 1, 0f), ("hip_jntBnd", 255, 255, -1000f)], []);

        SweepResult said = Swept(install, "quiet.ao");

        Assert.Contains("apart", said.Tally.Flags.Keys);

        // And the monster whose piece sits on him is not named - the flag distinguishes, which
        // is the half a vacuous check gets right for free.
        Assert.DoesNotContain("apart", Swept(Dressed(), "quiet.ao").Tally.Flags.Keys);
    }

    /// <summary>
    /// A piece whose socket the monster's rig has no bone for is flagged dropped.
    /// </summary>
    /// <remarks>
    /// THE ONE ANSWER THAT LOSES A PIECE OUTRIGHT: at the top level there is no carrier to fall
    /// back to, so the piece is left out entirely. Nothing downstream ever sees it again, which
    /// is exactly why the walk has to say so at the moment it decides - a monster missing a piece
    /// looks in every other respect like a monster that never had one.
    /// </remarks>
    [Fact]
    public void APieceSocketedToABoneTheRigHasNotIsFlaggedDropped()
    {
        Fake install = Dressed();
        install.Files["lost.ao"] = Ao(
            skin: "art/mesh.sm",
            attach: "art/cloak.ao",
            socket: "a_bone_nobody_has_jntBnd",
            skeleton: "art/rig.ast");

        SweepResult said = Swept(install, "lost.ao");

        Assert.Contains("dropped", said.Tally.Flags.Keys);
    }

    /// <summary>
    /// A piece hung off another piece is flagged deep, and its depth is in the record.
    /// </summary>
    /// <remarks>
    /// TYCHO'S SKIRT LAYERS. Until 0.1.21 a nested piece was fitted against the MONSTER's rig
    /// rather than its carrier's, and the whole class of them was invisible because nobody counts
    /// how deep an attachment tree goes while reading one monster's dump. The flag is what makes
    /// "how many monsters even have one" a question with an answer.
    /// </remarks>
    [Fact]
    public void APieceHungOffAPieceIsFlaggedDeep()
    {
        Fake install = Dressed();
        install.Files["art/cloak.ao"] = Ao(
            skin: "art/cloak.sm", attach: "art/trim.ao", socket: "<root>", skeleton: "art/cloak.ast");
        install.Files["art/trim.ao"] = Ao(skin: "art/trim.sm", skeleton: "art/cloak.ast");
        install.Files["art/trim.sm"] = Sm("art/trim.smd");
        install.Files["art/trim.smd"] = Packed.Mesh(Vertices(1));

        SweepResult said = Swept(install, "quiet.ao");

        Assert.Contains("deep", said.Tally.Flags.Keys);
        Assert.Contains("\"depth\":2", Lines(install, "quiet.ao")[0]);
    }

    /// <summary>
    /// A merged rig's pathed bone names are counted, and the monster is flagged.
    /// </summary>
    /// <remarks>
    /// BRUGHOR'S CORPSE ARMOUR is eighty bones of merged skeletons whose names read
    /// <c>root_jntBnd|spine_2_jntBnd</c>. There is no reason to think he is the only one, and
    /// counting them across the table is the only way anybody finds out.
    /// </remarks>
    [Fact]
    public void AMergedRigsPathedBoneNamesAreCountedAndFlagged()
    {
        Fake install = Dressed();
        install.Files["art/cloak.ast"] = Packed.Skeleton(
            [("root_jntBnd", 255, 1, 0f), ("root_jntBnd|hip_jntBnd", 255, 255, -30f)], []);

        SweepResult said = Swept(install, "quiet.ao");

        Assert.Contains("paths", said.Tally.Flags.Keys);
        Assert.Contains("\"paths\":1", Lines(install, "quiet.ao")[0]);
    }

    /// <summary>
    /// An attachment_bones line ending in something that is not a bone group is flagged.
    /// </summary>
    /// <remarks>
    /// TYCHO'S SkirtLayers.ao IS A COPY-PASTE: its attachment_bones is the bone_group line
    /// beneath it, verbatim, so the last name on it is a BONE and the pairing resolves to
    /// nothing. The walk falls back to matching by name and carries on, which is the right thing
    /// to do and also the reason nobody would ever notice. Counted, so somebody can.
    /// </remarks>
    [Fact]
    public void AnAttachmentBonesLineNamingNoGroupIsFlaggedMalformed()
    {
        Fake install = Dressed();
        install.Files["art/cloak.ao"] = Encoding.UTF8.GetBytes(
            "version 3\nclient\n{\n"
            + "\tClientAnimationController\n\t{\n"
            + "\t\tskeleton = \"art/cloak.ast\"\n"
            + "\t\tattachment_bones = \"child_attach false root_jntBnd hip_jntBnd \"\n"
            + "\t}\n"
            + "\tBoneGroups\n\t{\n"
            + "\t\tbone_group = \"child_attach false root_jntBnd hip_jntBnd \"\n"
            + "\t}\n"
            + "\tSkinMesh\n\t{\n\t\tskin = \"art/cloak.sm\"\n\t}\n}\n");

        SweepResult said = Swept(install, "quiet.ao");

        Assert.Contains("malformed", said.Tally.Flags.Keys);
        Assert.Contains("\"malformed\":true", Lines(install, "quiet.ao")[0]);
    }

    /// <summary>
    /// The sweep asks the install for no picture at all.
    /// </summary>
    /// <remarks>
    /// WHAT MAKES A RUN OVER THE WHOLE TABLE FINISH. Doryani alone is 32 MB across 15 files and
    /// the 2048-square sheets are nearly all of it; over 2733 monsters that is the difference
    /// between an afternoon and a coffee. Asserted rather than assumed, because the saving is
    /// invisible in the output - a sweep that quietly started decoding textures would look
    /// exactly like this one, only slower, and nobody would know why.
    /// </remarks>
    [Fact]
    public void TheSweepDecodesNoPictures()
    {
        Fake install = Dressed();
        var asked = new List<string>();

        _ = ModelSweep.Of(
            path =>
            {
                asked.Add(path);
                return install.Read(path);
            },
            Table(install),
            string.Empty);

        Assert.NotEmpty(asked);
        Assert.DoesNotContain(asked, one => one.EndsWith(".dds", StringComparison.OrdinalIgnoreCase));

        // And it DID read the files that decide where a piece goes, so the absence above is the
        // pictures being skipped and not the walk having stopped early.
        Assert.Contains(asked, one => one.EndsWith(".ast", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(asked, one => one.EndsWith(".smd", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The corpus is one line per monster, sorted by path, whatever order the table is in.
    /// </summary>
    /// <remarks>
    /// SO TWO BUILDS DIFF. A dictionary's order is not a promise, and a corpus that reshuffles
    /// itself between runs turns "what changed" into a question no diff can answer - which is
    /// most of what this file is for once there is more than one of it.
    /// </remarks>
    [Fact]
    public void TheCorpusIsOneLinePerMonsterSortedByPath()
    {
        Fake install = Dressed();
        install.Files["a.ao"] = Ao(skin: "art/mesh.sm", skeleton: "art/rig.ast");
        install.Files["z.ao"] = Ao(skin: "art/mesh.sm", skeleton: "art/rig.ast");

        var table = MonsterVarieties.From(
            [
                Named("Metadata/Monsters/Z", "z.ao"),
                Named("Metadata/Monsters/A", "a.ao"),
                Named("Metadata/Monsters/M", "quiet.ao"),
            ],
            new Dictionary<int, string>(),
            new Dictionary<int, ModifierMeaning>(),
            new Dictionary<int, string>(),
            new Dictionary<int, MonsterKind>(),
            new Dictionary<int, string>(),
            new Dictionary<int, string>(),
            "a test");

        var lines = new List<string>();
        SweepResult said = ModelSweep.Of(install.Read, table, string.Empty, lines.Add);

        Assert.Equal(3, said.Tally.Walked);
        Assert.Equal(3, lines.Count);
        Assert.Contains("\"path\":\"Metadata/Monsters/A\"", lines[0]);
        Assert.Contains("\"path\":\"Metadata/Monsters/M\"", lines[1]);
        Assert.Contains("\"path\":\"Metadata/Monsters/Z\"", lines[2]);
        Assert.All(lines, one => Assert.DoesNotContain('\n', one));
    }

    /// <summary>A monster the table names no .ao for is counted apart, not walked.</summary>
    [Fact]
    public void AMonsterWithNoAoFileIsCountedRatherThanWalked()
    {
        Fake install = Dressed();
        var table = MonsterVarieties.From(
            [new KeyValuePair<string, MonsterVariety>("Metadata/Monsters/Bare", new MonsterVariety("Bare"))],
            new Dictionary<int, string>(),
            new Dictionary<int, ModifierMeaning>(),
            new Dictionary<int, string>(),
            new Dictionary<int, MonsterKind>(),
            new Dictionary<int, string>(),
            new Dictionary<int, string>(),
            "a test");

        var lines = new List<string>();
        SweepResult said = ModelSweep.Of(install.Read, table, string.Empty, lines.Add);

        Assert.Equal(0, said.Tally.Walked);
        Assert.Equal(1, said.Tally.Nameless);
        Assert.Empty(lines);
    }

    /// <summary>No install and no table is an empty answer rather than a crash.</summary>
    [Fact]
    public void NothingToReadIsAnEmptyAnswer()
    {
        Assert.Empty(ModelSweep.Of(null, null, string.Empty).Flagged);
        Assert.Equal(0, ModelSweep.Of(null, MonsterVarieties.Empty, string.Empty).Tally.Walked);
    }

    /// <summary>The lines one monster produces, for asserting on the record itself.</summary>
    private static List<string> Lines(Fake install, string ao)
    {
        var lines = new List<string>();
        _ = ModelSweep.Of(install.Read, Table(install, ao), string.Empty, lines.Add);
        return lines;
    }

    private static SweepResult Swept(Fake install, string ao)
        => ModelSweep.Of(install.Read, Table(install, ao), string.Empty);

    private static MonsterVarieties Table(Fake install, string ao = "quiet.ao")
    {
        _ = install;
        return MonsterVarieties.From(
            [Named("Metadata/Monsters/Test", ao)],
            new Dictionary<int, string>(),
            new Dictionary<int, ModifierMeaning>(),
            new Dictionary<int, string>(),
            new Dictionary<int, MonsterKind>(),
            new Dictionary<int, string>(),
            new Dictionary<int, string>(),
            "a test");
    }

    private static KeyValuePair<string, MonsterVariety> Named(string path, string ao)
        => new(path, new MonsterVariety(Name: path[(path.LastIndexOf('/') + 1)..], AoFiles: [ao]));

    /// <summary>
    /// A monster wearing one piece that finds its bones - the baseline the flags are against.
    /// </summary>
    private static Fake Dressed()
    {
        var install = new Fake();
        install.Files["art/rig.ast"] = Packed.Skeleton(
            [("root_jntBnd", 255, 1, 0f), ("hip_jntBnd", 255, 255, -150f)], []);
        install.Files["art/cloak.ast"] = Packed.Skeleton(
            [("root_jntBnd", 255, 1, 0f), ("hip_jntBnd", 255, 255, -150f)], []);

        install.Files["quiet.ao"] = Ao(
            skin: "art/mesh.sm", attach: "art/cloak.ao", socket: "hip_jntBnd", skeleton: "art/rig.ast");
        install.Files["art/cloak.ao"] = Ao(skin: "art/cloak.sm", skeleton: "art/cloak.ast");

        // A BODY WITH EXTENT, not a flat sheet: a piece is held up against the monster's own box
        // and a monster who is one quad at one depth is apart from everything he wears. The first
        // fixture here was exactly that, and it flagged the baseline.
        install.Files["art/mesh.sm"] = Sm("art/body.smd");
        install.Files["art/body.smd"] = Packed.Mesh(
        [
            new Packed.Vertex(new Vector3(-40f, -40f, 0f), [0, 0, 0, 0], [255, 0, 0, 0]),
            new Packed.Vertex(new Vector3(40f, -40f, -60f), [0, 0, 0, 0], [255, 0, 0, 0]),
            new Packed.Vertex(new Vector3(40f, 40f, -120f), [1, 0, 0, 0], [255, 0, 0, 0]),
            new Packed.Vertex(new Vector3(-40f, 40f, -180f), [1, 0, 0, 0], [255, 0, 0, 0]),
        ]);

        // And the piece is authored around ITS own origin, so its socket - the hip, a hundred
        // and fifty down - is what carries it onto him.
        install.Files["art/cloak.sm"] = Sm("art/cloak.smd");
        install.Files["art/cloak.smd"] = Packed.Mesh(Vertices(1));
        install.Files["art/paint.mat"] = Encoding.UTF8.GetBytes(
            """{"graphinstances":[{"custom_parameters":[{"name":"AlbedoTransparency_TEX","parameters":[{"path":"art/skin.dds"}]}]}]}""");
        install.Files["art/skin.dds"] = new byte[128];
        return install;
    }

    /// <summary>Four vertices around the origin on one bone - a mesh, and never drawn here.</summary>
    private static Packed.Vertex[] Vertices(byte bone) =>
    [
        .. new[] { -10f, -3f, 3f, 10f }.Select(one =>
            new Packed.Vertex(new Vector3(one, 0f, one), [bone, 0, 0, 0], [255, 0, 0, 0])),
    ];

    private static byte[] Sm(string geometry) => Encoding.UTF8.GetBytes(
        $"version 6\nSkinnedMeshData \"{geometry}\"\nMaterials 1\n\t\"art/paint.mat\" 1\n"
        + "BoundingBox -1 -1 -1 1 1 1\n");

    private static byte[] Ao(
        string? skin = null,
        string? attach = null,
        string socket = "hip_jntBnd",
        string? skeleton = null)
    {
        var said = new StringBuilder("version 3\n");
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
            said.Append("SkinMesh\n{\n\tskin = \"").Append(skin).Append("\"\n}\n");
        }

        return Encoding.UTF8.GetBytes(said.ToString());
    }

    /// <summary>A stand-in install: the paths a walk may ask for, and their bytes.</summary>
    private sealed class Fake
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public byte[]? Read(string path) => Files.TryGetValue(path, out byte[]? said) ? said : null;
    }
}
