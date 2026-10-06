using PoEformance.Features;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// The install's names for rewards, and what an absent install leaves.
/// </summary>
/// <remarks>
/// SYNTHETIC .dat FILES against the VENDORED layouts in data/item-tables.json, the way
/// ItemVisualsTests does it: what is checked is the join, and whether the layouts fit a real
/// install is settled at runtime by each file's own row size.
/// </remarks>
public class RewardCatalogTests
{
    internal static QuestTableLayouts Layouts()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "item-tables.json")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Assert.IsType<QuestTableLayouts>(
            QuestTableLayouts.Load(Path.Combine(dir!.FullName, "data", "item-tables.json")));
    }

    private static int At(QuestTableLayouts layouts, string table, string column)
    {
        int at = layouts.OffsetOf(table, column);
        Assert.True(at >= 0, $"the vendored layout has no {table}.{column}");
        return at;
    }

    /// <summary>
    /// A catalogue holding exactly these rows, read out of a made-up install the way the real
    /// one is read.
    /// </summary>
    /// <remarks>
    /// Through a fake install rather than a constructor, because the catalogue has no public
    /// constructor on purpose - its one maker is the table read, and a fixture that bypassed it
    /// would test a catalogue the tool can never produce. A row with no picture gets a NULL
    /// visual reference, which is the shape the file writes for one.
    /// </remarks>
    internal static RewardCatalog With(params (string Path, RewardName Named)[] rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        QuestTableLayouts layouts = Layouts();

        int pictured = rows.Count(row => row.Named.Art.Length > 0);
        var bases = new FakeDat(rows.Length, layouts.RowSizeOf("BaseItemTypes"));
        var art = new FakeDat(Math.Max(1, pictured), layouts.RowSizeOf("ItemVisualIdentity"));

        var next = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            (string path, RewardName named) = rows[i];
            bases
                .Text(i, At(layouts, "BaseItemTypes", "Id"), path)
                .Text(i, At(layouts, "BaseItemTypes", "Name"), named.Name);

            if (named.Art.Length > 0)
            {
                art
                    .Text(next, At(layouts, "ItemVisualIdentity", "Id"), $"Art{next}")
                    .Text(next, At(layouts, "ItemVisualIdentity", "DDSFile"), named.Art);
                bases.Reference(i, At(layouts, "BaseItemTypes", "ItemVisualIdentity"), next);
                next++;
            }
            else
            {
                bases.Null(i, At(layouts, "BaseItemTypes", "ItemVisualIdentity"));
            }
        }

        GameFiles? install = FakeInstall.Of(
            ("data/baseitemtypes.datc64", bases.Bytes()),
            ("data/itemvisualidentity.datc64", art.Bytes()));
        Assert.NotNull(install);

        RewardCatalog catalog = RewardCatalog.Read(install, layouts);
        Assert.Equal(rows.Length, catalog.Count);
        return catalog;
    }

    [Fact]
    public void NamesAndPicturesComeOutOfTheTwoTables()
    {
        RewardCatalog catalog = With(
            ("Metadata/Items/Currency/CurrencyUpgradeMagicToRare2", new RewardName("Greater Regal Orb", "Art/2DItems/Currency/CurrencyUpgradeMagicToRare.dds")),
            ("Metadata/Items/Currency/CurrencyDuplicate", new RewardName("Mirror of Kalandra", "")));

        Assert.Equal(
            new RewardName("Greater Regal Orb", "Art/2DItems/Currency/CurrencyUpgradeMagicToRare.dds"),
            catalog.Of("Metadata/Items/Currency/CurrencyUpgradeMagicToRare2"));
        Assert.Equal(new RewardName("Mirror of Kalandra", ""), catalog.Of("Metadata/Items/Currency/CurrencyDuplicate"));
        Assert.Null(catalog.Of("Metadata/Items/Currency/Nothing"));
        Assert.Null(catalog.Of(null));
        Assert.Contains(catalog.Say, line => line.Contains("2 base types named", StringComparison.Ordinal));
        Assert.Contains(catalog.Say, line => line.Contains("1 with a picture", StringComparison.Ordinal));
    }

    [Fact]
    public void NoInstallLeavesAnEmptyCatalogueThatSaysSo()
    {
        RewardCatalog none = RewardCatalog.Read(null, Layouts());
        Assert.Equal(0, none.Count);
        Assert.Contains(none.Say, line => line.Contains("no install", StringComparison.Ordinal));

        RewardCatalog unlaid = RewardCatalog.Read(FakeInstall.Of(), null);
        Assert.Equal(0, unlaid.Count);
        Assert.Contains(unlaid.Say, line => line.Contains("item-tables.json", StringComparison.Ordinal));

        RewardCatalog empty = RewardCatalog.Empty;
        Assert.Equal(0, empty.Count);
        Assert.Null(empty.Of("Metadata/Items/Currency/CurrencyDuplicate"));
    }
}
