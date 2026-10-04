using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// Where exported boss pictures go on the icon sheet - the rule the overlay and the bake share.
/// </summary>
/// <remarks>
/// What these hold is the set of mistakes that would be invisible afterwards: a cell number
/// that moves between the live sheet and the baked one, the game's art painted over, a pair
/// split across a row, and an Inactive file mistaken for an Active one.
/// </remarks>
public class IconSheetGrowthTests
{
    /// <summary>The real sheet's width: fourteen columns.</summary>
    private const int Columns = 14;

    /// <summary>The first cell past the game's rows, counted from zero.</summary>
    private const int Own = IconSheet.OwnRow * Columns;

    private const int Most = IconSheet.MaxEdge / IconSheet.Tile;

    [Theory]
    [InlineData("IgnagdukBossActive.png", "IgnagdukBoss")]
    [InlineData("tool/exports/IgnagdukBossActive.png", "IgnagdukBoss")]
    [InlineData("IgnagdukBossInactive.png", "")]
    [InlineData("IgnagdukBossActive-1024.png", "")]
    [InlineData("IgnagdukBossInactive-1024.png", "")]
    [InlineData("IgnagdukBoss.files.txt", "")]
    [InlineData("Active.png", "")]
    public void AFileIsTheActivePictureOfAFamilyOrOfNothing(string file, string family)
    {
        Assert.Equal(family, IconSheetGrowth.FamilyOf(file));
    }

    /// <summary>
    /// A family whose name ends in "In" is not mistaken for an Inactive picture, or the reverse.
    /// </summary>
    /// <remarks>
    /// The plain wildcard this replaced matched FooInactive.png as the Active of "FooIn". Case
    /// decides it: the export writes the suffix in one spelling only.
    /// </remarks>
    [Fact]
    public void TheSuffixIsReadInTheOneSpellingTheExportWrites()
    {
        Assert.Equal("BossIn", IconSheetGrowth.FamilyOf("BossInActive.png"));
        Assert.Equal(string.Empty, IconSheetGrowth.FamilyOf("BossInactive.png"));
    }

    [Fact]
    public void FamiliesAreSortedAndCountedOnce()
    {
        List<string> families = IconSheetGrowth.Families(
        [
            "ZetaActive.png", "alphaActive.png", "ZetaInactive.png", "AlphaActive.png", "ZetaActive-1024.png",
        ]);

        Assert.Equal(["alpha", "Zeta"], families);
    }

    [Fact]
    public void AnEmptySheetHasNoOccupiedCell()
    {
        byte[] sheet = new byte[128 * 128 * 4];
        Assert.Equal(-1, IconSheetGrowth.LastOccupied(sheet, 128, 128));
    }

    [Fact]
    public void TheLastOccupiedCellIsFoundByAlpha()
    {
        byte[] sheet = new byte[128 * 128 * 4];

        // Bottom-left cell (index 2 of a 2x2 sheet), one pixel deep inside it.
        int pixel = ((64 + 40) * 128) + 10;
        sheet[(pixel * 4) + 3] = 1;
        Assert.Equal(2, IconSheetGrowth.LastOccupied(sheet, 128, 128));
    }

    /// <summary>A transparent pixel with colour in it draws nothing, so it holds no art.</summary>
    [Fact]
    public void ColourUnderZeroAlphaIsNotArt()
    {
        byte[] sheet = new byte[128 * 128 * 4];
        int pixel = (100 * 128) + 100;
        sheet[pixel * 4] = 255;
        sheet[(pixel * 4) + 1] = 255;
        Assert.Equal(-1, IconSheetGrowth.LastOccupied(sheet, 128, 128));
    }

    /// <summary>
    /// The first new pair starts on row 78, and the empty tail of row 77 stays empty.
    /// </summary>
    /// <remarks>Measured on the shipped sheet: its last art is cell 1059, counted from one.</remarks>
    [Fact]
    public void TheFirstPairStartsPastTheGamesRows()
    {
        Assert.Equal(Own, IconSheetGrowth.FirstFree(Columns, lastOccupied: 1058, lastNamed: 1046));
    }

    [Fact]
    public void LaterPairsFollowTheArtAndStayAligned()
    {
        Assert.Equal(Own + 2, IconSheetGrowth.FirstFree(Columns, lastOccupied: Own + 1, lastNamed: -1));

        // An Active painted and its Inactive never exported: the empty cell beside it is still
        // that boss's, and the next pair starts after it.
        Assert.Equal(Own + 2, IconSheetGrowth.FirstFree(Columns, lastOccupied: Own, lastNamed: -1));
        Assert.Equal(Own + 4, IconSheetGrowth.FirstFree(Columns, lastOccupied: Own, lastNamed: Own + 3));
    }

    /// <summary>On an odd number of columns a pair never starts in the last one.</summary>
    [Fact]
    public void APairIsNeverSplitAcrossARow()
    {
        Assert.Equal(15, IconSheetGrowth.Aligned(14, 15));
        Assert.Equal(15, IconSheetGrowth.Aligned(13, 15));
        Assert.Equal(16, IconSheetGrowth.Aligned(15, 16));
    }

    [Fact]
    public void NewFamiliesFillPairsInTheOrderGiven()
    {
        IconGrowth growth = IconSheetGrowth.Place(["Alpha", "Beta"], _ => 0, Columns, Own, Most);

        Assert.Equal(
            [new IconPlacement("Alpha", Own, Own + 1, true), new IconPlacement("Beta", Own + 2, Own + 3, true)],
            growth.Placed);
        Assert.Empty(growth.Game);
        Assert.Empty(growth.Full);
    }

    /// <summary>
    /// Art the game carries is never painted over, whichever of its names it is under.
    /// </summary>
    [Theory]
    [InlineData("IgnagdukBossActive")]
    [InlineData("IgnagdukBossInactive")]
    [InlineData("IgnagdukBoss")]
    public void TheGamesArtIsLeftAlone(string carried)
    {
        IconGrowth growth = IconSheetGrowth.Place(
            ["IgnagdukBoss"], name => name == carried ? 588 : 0, Columns, Own, Most);

        Assert.Empty(growth.Placed);
        Assert.Equal(["IgnagdukBoss"], growth.Game);
    }

    /// <summary>A boss posed again goes into the cells it already has, and nothing moves.</summary>
    [Fact]
    public void AReExportPaintsOverItsOwnCells()
    {
        int active = Own + 4 + 1;   // counted from one, as the table stores it
        IconGrowth growth = IconSheetGrowth.Place(
            ["Saphira", "Zeta"],
            name => name switch { "SaphiraActive" => active, "SaphiraInactive" => active + 1, _ => 0 },
            Columns,
            Own + 6,
            Most);

        Assert.Equal(
            [new IconPlacement("Saphira", Own + 4, Own + 5, false), new IconPlacement("Zeta", Own + 6, Own + 7, true)],
            growth.Placed);
    }

    [Fact]
    public void WhatDoesNotFitIsReportedNotSqueezed()
    {
        int last = (Most * Columns) - 2;
        IconGrowth growth = IconSheetGrowth.Place(["Alpha", "Beta"], _ => 0, Columns, last, Most);

        Assert.Equal([new IconPlacement("Alpha", last, last + 1, true)], growth.Placed);
        Assert.Equal(["Beta"], growth.Full);
    }

    [Fact]
    public void TheSheetGrowsByWholeRowsAndNeverShrinks()
    {
        var growth = new IconGrowth([new IconPlacement("Alpha", Own, Own + 1, true)], [], []);
        Assert.Equal(IconSheet.OwnRow + 1, growth.RowsFor(Columns, IconSheet.OwnRow));
        Assert.Equal(90, growth.RowsFor(Columns, 90));
    }

    [Fact]
    public void ACellIsCopiedToItsPlaceAndNowhereElse()
    {
        const int Width = 128;
        byte[] sheet = new byte[Width * 128 * 4];
        byte[] cell = new byte[IconSheet.Tile * IconSheet.Tile * 4];
        Array.Fill(cell, (byte)7);

        IconSheetGrowth.Copy(sheet, Width, cell, 3);

        Assert.Equal(3, IconSheetGrowth.LastOccupied(sheet, Width, 128));
        Assert.Equal(0, sheet[(((64 * Width) + 63) * 4) + 3]);           // last pixel of cell 2
        Assert.Equal(7, sheet[(((64 * Width) + 64) * 4) + 3]);           // first pixel of cell 3
        Assert.Equal(7, sheet[^1]);                                     // last pixel of cell 3
        Assert.Equal(64 * 64 * 4, sheet.Count(b => b == 7));
    }

    [Fact]
    public void ACellOfTheWrongSizeIsRefused()
    {
        byte[] sheet = new byte[128 * 128 * 4];
        Assert.Throws<ArgumentException>(() => IconSheetGrowth.Copy(sheet, 128, new byte[10], 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => IconSheetGrowth.Copy(sheet, 128, new byte[64 * 64 * 4], 4));
    }
}
