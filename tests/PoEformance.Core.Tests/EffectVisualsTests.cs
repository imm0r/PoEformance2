using System.Text;
using PoEformance.Features;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// The effect book's table, joined out of synthetic copies of the install's own, and the shader
/// graphs a material names.
/// </summary>
/// <remarks>
/// AGAINST THE VENDORED LAYOUTS in data/effect-tables.json, the choice the item and monster tables
/// made: this checks the join, and the file's own row size checks the layout at runtime.
/// </remarks>
public class EffectVisualsTests
{
    private static QuestTableLayouts Layouts()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "effect-tables.json")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Assert.IsType<QuestTableLayouts>(
            QuestTableLayouts.Load(Path.Combine(dir!.FullName, "data", "effect-tables.json")));
    }

    private static int At(QuestTableLayouts layouts, string table, string column)
    {
        int at = layouts.OffsetOf(table, column);
        Assert.True(at >= 0, $"the vendored layout has no {table}.{column}");
        return at;
    }

    [Fact]
    public void THEVENDOREDLayoutsComputeTheSizesTheirCommentRecords()
    {
        QuestTableLayouts layouts = Layouts();

        Assert.Equal(368, layouts.RowSizeOf("Projectiles"));
        Assert.Equal(44, layouts.RowSizeOf("MiscAnimated"));
        Assert.Equal(235, layouts.RowSizeOf("GroundEffects"));
        Assert.Equal(64, layouts.RowSizeOf("GroundEffectTypes"));
    }

    private static GameFiles Install(QuestTableLayouts layouts)
    {
        var projectiles = new FakeDat(2, layouts.RowSizeOf("Projectiles"));
        var animated = new FakeDat(2, layouts.RowSizeOf("MiscAnimated"));
        var ground = new FakeDat(3, layouts.RowSizeOf("GroundEffects"));
        var types = new FakeDat(1, layouts.RowSizeOf("GroundEffectTypes"));

        projectiles
            .Text(0, At(layouts, "Projectiles", "Id"), "Fireball")
            .Texts(0, At(layouts, "Projectiles", "AOFiles"), "Metadata/Projectiles/Fireball.ao", "Metadata/Projectiles/FireballAlt.ao")
            .Texts(0, At(layouts, "Projectiles", "Stuck_AOFile"), "Metadata/Projectiles/FireballStuck.ao")
            .Text(0, At(layouts, "Projectiles", "Bounce_AOFile"), "Metadata/Projectiles/FireballBounce.ao")

            // A projectile with no art at all - an engine projectile - is left out.
            .Text(1, At(layouts, "Projectiles", "Id"), "Invisible")
            .Texts(1, At(layouts, "Projectiles", "AOFiles"))
            .Texts(1, At(layouts, "Projectiles", "Stuck_AOFile"));

        animated
            .Text(0, At(layouts, "MiscAnimated", "Id"), "FrostNova")
            .Text(0, At(layouts, "MiscAnimated", "AOFile"), "Metadata/Effects/FrostNova.ao")
            .Text(1, At(layouts, "MiscAnimated", "Id"), "NoArt");

        types.Text(0, At(layouts, "GroundEffectTypes", "Id"), "Burning");

        ground
            .Reference(0, At(layouts, "GroundEffects", "GroundEffectTypesKey"), 0)
            .Texts(0, At(layouts, "GroundEffects", "AOFile"), "Metadata/Effects/BurningGround.ao")
            .Reference(1, At(layouts, "GroundEffects", "GroundEffectTypesKey"), 0)
            .Texts(1, At(layouts, "GroundEffects", "AOFile"), "Metadata/Effects/BurningGroundLarge.ao")
            .Null(2, At(layouts, "GroundEffects", "GroundEffectTypesKey"))
            .Texts(2, At(layouts, "GroundEffects", "AOFile"));

        GameFiles? files = FakeInstall.Of(
            ("data/projectiles.datc64", projectiles.Bytes()),
            ("data/miscanimated.datc64", animated.Bytes()),
            ("data/groundeffects.datc64", ground.Bytes()),
            ("data/groundeffecttypes.datc64", types.Bytes()));
        Assert.NotNull(files);
        return files!;
    }

    [Fact]
    public void APROJECTILEOffersEveryFileItNamesInColumnOrder()
    {
        QuestTableLayouts layouts = Layouts();
        EffectVisuals effects = EffectVisuals.Read(Install(layouts), layouts);

        EffectVisual? fireball = effects.Find("projectile/Fireball");
        Assert.NotNull(fireball);
        Assert.Equal(
            [
                new EffectFile("art 1", "Metadata/Projectiles/Fireball.ao"),
                new EffectFile("art 2", "Metadata/Projectiles/FireballAlt.ao"),
                new EffectFile("stuck", "Metadata/Projectiles/FireballStuck.ao"),
                new EffectFile("bounce", "Metadata/Projectiles/FireballBounce.ao"),
            ],
            fireball!.Files);
        Assert.Null(effects.Find("projectile/Invisible"));
    }

    [Fact]
    public void ANDAnimatedAndGroundEffectsAreRowsOfTheirOwnNamedByTheirIdOrType()
    {
        QuestTableLayouts layouts = Layouts();
        EffectVisuals effects = EffectVisuals.Read(Install(layouts), layouts);

        Assert.NotNull(effects.Find("animated/FrostNova"));
        Assert.Null(effects.Find("animated/NoArt"));

        // TWO ROWS OF ONE TYPE: the first takes the type's name, the second gets its row number too.
        Assert.NotNull(effects.Find("ground/Burning"));
        Assert.NotNull(effects.Find("ground/Burning #1"));
        Assert.Equal(4, effects.Count);
        Assert.Contains(effects.Say, line => line.Contains("1 projectiles, 1 animated effects and 2 ground effects", StringComparison.Ordinal));
    }

    [Fact]
    public void THEBOOKFindsByKindAndByHowManyFilesAnEffectHas()
    {
        QuestTableLayouts layouts = Layouts();
        EffectBook book = EffectBook.Of(EffectVisuals.Read(Install(layouts), layouts));

        Assert.Equal(["Fireball"], Names(book, "kind:projectile"));
        Assert.Equal(["Fireball"], Names(book, "files>1"));
        Assert.Equal(["Burning", "Burning #1"], Names(book, "kind:ground"));
        Assert.Equal(["Fireball"], Names(book, "stuck"));
    }

    [Fact]
    public void ANDNoInstallSaysSo()
    {
        EffectVisuals effects = EffectVisuals.Read(null, null);

        Assert.Equal(0, effects.Count);
        Assert.Contains(effects.Say, line => line.Contains("no install", StringComparison.Ordinal));
    }

    [Fact]
    public void AMATERIALNamesTheShaderGraphOfEachInstanceAndAModelCollectsThem()
    {
        const string Material = """
            {"version":4,"graphinstances":[
              {"parent":"Metadata/Effects/Graphs/AdditiveBlend.fxgraph","custom_parameters":[{"name":"AlbedoTransparency_TEX","parameters":[{"path":"art/fire.dds"}]}]},
              {"parent":"Metadata/Effects/Graphs/Opaque.fxgraph"}
            ]}
            """;

        MaterialFile read = MaterialFile.Parse(Material);
        Assert.Equal(["Metadata/Effects/Graphs/AdditiveBlend.fxgraph", "Metadata/Effects/Graphs/Opaque.fxgraph"], read.Parents);

        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["fire.ao"] = Encoding.UTF8.GetBytes("version 3\nclient\n{\n\tFixedMesh\n\t{\n\t\tfixed_mesh = \"art/fire.fmt\"\n\t}\n}\n"),
            ["art/fire.fmt"] = Packed.Fmt("art/fire.mat"),
            ["art/fire.mat"] = Encoding.UTF8.GetBytes(Material),
        };

        MonsterModel model = MonsterModels.OfFiles(path => files.GetValueOrDefault(path), ["fire.ao"]);
        Assert.True(model.Ready, model.Why);
        Assert.Equal(["Metadata/Effects/Graphs/AdditiveBlend.fxgraph", "Metadata/Effects/Graphs/Opaque.fxgraph"], model.Shaders);
    }

    [Theory]
    [InlineData("AdditiveBlend", MaterialBlend.Additive)]
    [InlineData("Additive", MaterialBlend.Additive)]
    [InlineData("AlphaBlend", MaterialBlend.Alpha)]
    [InlineData("AlphaTestWithShadow", MaterialBlend.Cutout)]
    [InlineData("AlphaTest", MaterialBlend.Cutout)]
    [InlineData("Transparent", MaterialBlend.Alpha)]
    [InlineData("Opaque", MaterialBlend.Opaque)]
    [InlineData("", MaterialBlend.Opaque)]
    [InlineData(null, MaterialBlend.Opaque)]
    public void THEBLENDWordIsReadByTheChosenRuleWithAdditiveAskedFirst(string? mode, MaterialBlend expected)
        => Assert.Equal(expected, MaterialBlends.Of(mode));

    [Fact]
    public void AMATERIALSOwnBlendModeIsReadOffItsDefaultGraphAndAGraphFilesOffItsTop()
    {
        MaterialFile read = MaterialFile.Parse("""
            {"version":4,"defaultgraph":{"version":3,"nodes":[],"overriden_blend_mode":"AdditiveBlend"},
             "graphinstances":[{"parent":"Metadata/Effects/Graphs/Fire.fxgraph","custom_parameters":[{"name":"AlbedoTransparency_TEX","parameters":[{"path":"art/fire.dds"}]}]}]}
            """);
        Assert.Equal("AdditiveBlend", read.Blend);

        Assert.Equal("AlphaBlend", MaterialFile.GraphBlend(Encoding.UTF8.GetBytes("""{"version":3,"links":[],"overriden_blend_mode":"AlphaBlend"}""")));
        Assert.Equal(string.Empty, MaterialFile.GraphBlend(Encoding.UTF8.GetBytes("""{"version":3}""")));
        Assert.Equal(string.Empty, MaterialFile.GraphBlend(Encoding.UTF8.GetBytes("not json")));
    }

    [Theory]
    [InlineData(true, "AdditiveBlend")]
    [InlineData(false, "AlphaBlend")]
    public void AMODELSShapesCarryTheirMaterialsModeTheMaterialsOwnBeforeItsGraphs(bool own, string expected)
    {
        string material = own
            ? """{"version":4,"defaultgraph":{"version":3,"overriden_blend_mode":"AdditiveBlend"},"graphinstances":[{"parent":"graphs/glow.fxgraph","custom_parameters":[{"name":"AlbedoTransparency_TEX","parameters":[{"path":"art/fire.dds"}]}]}]}"""
            : """{"version":4,"defaultgraph":{"version":3},"graphinstances":[{"parent":"graphs/glow.fxgraph","custom_parameters":[{"name":"AlbedoTransparency_TEX","parameters":[{"path":"art/fire.dds"}]}]}]}""";

        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["fire.ao"] = Encoding.UTF8.GetBytes("version 3\nclient\n{\n\tFixedMesh\n\t{\n\t\tfixed_mesh = \"art/fire.fmt\"\n\t}\n}\n"),
            ["art/fire.fmt"] = Packed.Fmt("art/fire.mat"),
            ["art/fire.mat"] = Encoding.UTF8.GetBytes(material),
            ["graphs/glow.fxgraph"] = Encoding.UTF8.GetBytes("""{"version":3,"overriden_blend_mode":"AlphaBlend"}"""),
        };

        MonsterModel model = MonsterModels.OfFiles(path => files.GetValueOrDefault(path), ["fire.ao"]);

        Assert.True(model.Ready, model.Why);
        Assert.Equal([expected], model.Modes);
        Assert.Equal([MaterialBlends.Of(expected)], model.Blends);
    }

    private static string[] Names(EffectBook book, string query)
    {
        QueryResult parsed = ColumnQuery.Parse(query);
        Assert.Empty(parsed.Error);

        RowSet? rows = book.Matching(parsed.Term, out string why);
        Assert.True(rows is not null, why);

        var shown = new List<int>();
        rows!.CopyTo(shown);
        return [.. shown.Select(row => book.Store.Columns[0].Text[row]).Order(StringComparer.Ordinal)];
    }
}
