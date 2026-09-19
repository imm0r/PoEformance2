using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// Which of the sheet's boss pictures a boss arena resolves to.
/// </summary>
/// <remarks>
/// THE TEST THAT MATTERS IS THE ONE AGAINST THE REAL NAME TABLE, and it is at the bottom:
/// assets/icon-names.tsv is what the running tool looks a candidate up in, so a rule that
/// produces plausible strings and matches nothing in it is a rule that does nothing. The rest
/// of this file checks the ORDER and the shape of the candidates, which is what decides which
/// of several matches wins.
///
/// Nothing here asserts that a particular boss lives in a particular area. That is a claim
/// about the game, this cannot check it, and data/boss-icons.json is deliberately empty of
/// claims nobody has stood in the room for.
/// </remarks>
public class BossIconTests
{
    /// <summary>The shipped table of cell names - the same file the overlay reads.</summary>
    private static HashSet<string> SheetNames()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "assets", "icon-names.tsv")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadLines(Path.Combine(dir!.FullName, "assets", "icon-names.tsv")))
        {
            int tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab > 0 && !line.StartsWith('#'))
            {
                names.Add(line[(tab + 1)..].Trim());
            }
        }

        // Not vacuous: an empty or moved table would satisfy every assertion below by never
        // matching anything, which is the shape of check this project treats as no check.
        Assert.True(names.Count > 500, $"only {names.Count} cell names read - has the table moved?");
        return names;
    }

    /// <summary>A file with the given pairs, written where the test can reach it.</summary>
    private static BossIcons Written(string areas, string tiles)
    {
        string path = Path.Combine(Path.GetTempPath(), $"boss-icons-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{\"areas\": {" + areas + "}, \"tiles\": {" + tiles + "}}");
        try
        {
            return BossIcons.Load(path, Path.Combine(Path.GetTempPath(), $"arenas-{Guid.NewGuid():N}.tsv"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnAreaIdIsOfferedInBothSpellings()
    {
        IReadOnlyList<string> found = BossIcons.Empty.Candidates(
            "G4_3_1", "Metadata/Terrain/Islands/Tiles/TropicalIsland/Features/Shark_Arena_02.tdt");

        // Both, because the sheet carries both: G4_3_1_BossActive has the underscore and
        // G4_4_2BossActive does not.
        Assert.Contains("G4_3_1_Boss", found);
        Assert.Contains("G4_3_1Boss", found);
    }

    [Fact]
    public void TheTileFileNamesItsOwnBoss()
    {
        IReadOnlyList<string> found = BossIcons.Empty.Candidates(
            "MapSomewhere", "Metadata/Terrain/Maps/Somewhere/Plantaton_Boss_01.tdt");

        // The instance number and the separators go; "Boss" is already there and is not
        // doubled. This is the same name the marker is labelled with - "Plantaton Boss".
        Assert.Contains("PlantatonBoss", found);
    }

    [Fact]
    public void ATileNamedForTheRoomStillReachesTheBoss()
    {
        IReadOnlyList<string> found = BossIcons.Empty.Candidates(
            "MapKin", "Metadata/Terrain/Islands/IsleOfKin/Feature/IsleOfKinBossRoom_01.tdt");

        // The sheet files its art under the boss - IsleOfKinBossActive - while the tile is
        // named for the room it is. Both forms are offered, so either spelling finds it.
        Assert.Contains("IsleOfKinBoss", found);

        // Stacked words come off one at a time: Arena then Floor.
        Assert.Contains(
            "MausoleumBoss",
            BossIcons.Empty.Candidates(
                "G1_8", "Metadata/Terrain/Woods/GraveyardDungeons/Feature/MausoleumBoss_ArenaFloor.tdt"));
    }

    [Fact]
    public void AFolderNamedForABossIsOffered()
    {
        IReadOnlyList<string> found = BossIcons.Empty.Candidates(
            "G1_4", "Metadata/Terrain/Woods/GrimTangle/feature/BossArena_01.tdt");

        Assert.Contains("GrimTangleBoss", found);

        // And the folders that name nothing are not turned into candidates, so a match cannot
        // come from the word "Terrain" or "Features".
        Assert.DoesNotContain("TerrainBoss", found);
        Assert.DoesNotContain("FeatureBoss", found);
        Assert.DoesNotContain("TilesBoss", found);
    }

    [Fact]
    public void WhatWasWrittenDownBeatsWhatWasDerived()
    {
        BossIcons icons = Written(
            areas: """ "G4_3_1": "WrittenForTheArea" """,
            tiles: """ "Metadata/Terrain/X/Shark_Arena_02": "WrittenForTheTile" """);

        IReadOnlyList<string> found = icons.Candidates("G4_3_1", "Metadata/Terrain/X/Shark_Arena_02.tdt");

        // The tile first - an area can hold two arenas and the tile names THIS one - then the
        // area, then everything derived.
        Assert.Equal("WrittenForTheTile", found[0]);
        Assert.Equal("WrittenForTheArea", found[1]);
        Assert.Contains("G4_3_1_Boss", found);
    }

    [Fact]
    public void ATileKeyMatchesWithOrWithoutItsExtension()
    {
        BossIcons withDot = Written(
            areas: string.Empty, tiles: """ "Metadata/Terrain/X/Arena_01.tdt": "Written" """);
        BossIcons without = Written(
            areas: string.Empty, tiles: """ "Metadata/Terrain/X/Arena_01": "Written" """);

        // The path comes out of memory with the extension and the key was typed by hand, so
        // both spellings have to find each other in both directions.
        Assert.Equal("Written", withDot.Candidates("A", "Metadata/Terrain/X/Arena_01.tdt")[0]);
        Assert.Equal("Written", withDot.Candidates("A", "Metadata/Terrain/X/Arena_01")[0]);
        Assert.Equal("Written", without.Candidates("A", "Metadata/Terrain/X/Arena_01.tdt")[0]);
    }

    [Fact]
    public void ACandidateIsOfferedOnlyOnce()
    {
        // The area id and the tile file say the same thing here, which is the ordinary case
        // for a tile named after its area.
        IReadOnlyList<string> found = BossIcons.Empty.Candidates(
            "IsleOfKinBoss", "Metadata/Terrain/IsleOfKinBoss/IsleOfKinBoss_01.tdt");

        Assert.Equal(
            found.Count,
            found.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void TheStateDecidesTheSuffix()
    {
        Assert.Equal("IsleOfKinBossActive", BossIcons.Named("IsleOfKinBoss", cleared: false));
        Assert.Equal("IsleOfKinBossInactive", BossIcons.Named("IsleOfKinBoss", cleared: true));
    }

    [Fact]
    public void AMissingFileIsNotAFailure()
    {
        BossIcons icons = BossIcons.Load(
            Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.json"));

        Assert.Equal(0, icons.Count);

        // And it still answers: everything derived is still on offer, which is most of what
        // ever resolves.
        Assert.Contains("G4_7_Boss", icons.Candidates("G4_7", "Metadata/Terrain/X/Arena_01.tdt"));
    }

    [Fact]
    public void ABrokenFileCostsTheTableAndNothingElse()
    {
        string path = Path.Combine(Path.GetTempPath(), $"boss-icons-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ this is not json");
        try
        {
            BossIcons icons = BossIcons.Load(path);
            Assert.Equal(0, icons.Count);
            Assert.NotEmpty(icons.Candidates("G4_7", "Metadata/Terrain/X/Arena_01.tdt"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnArenaNothingCouldNameIsWrittenDownOnce()
    {
        string log = Path.Combine(Path.GetTempPath(), $"arenas-{Guid.NewGuid():N}.tsv");
        try
        {
            BossIcons icons = BossIcons.Load(
                Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.json"), log);

            Assert.True(icons.NoteMissing("G4_9", "Metadata/Terrain/X/Arena_01.tdt", "Boss Arena"));
            Assert.False(icons.NoteMissing("G4_9", "Metadata/Terrain/X/Arena_01.tdt", "Boss Arena"));
            Assert.Equal(1, icons.MissingCount);

            string written = File.ReadAllText(log);
            Assert.Contains("G4_9", written, StringComparison.Ordinal);
            Assert.Contains("Metadata/Terrain/X/Arena_01.tdt", written, StringComparison.Ordinal);

            // A second run reads what the first collected, or the file grows a line per
            // session for an arena that was noted months ago.
            BossIcons again = BossIcons.Load(
                Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.json"), log);
            Assert.False(again.NoteMissing("G4_9", "Metadata/Terrain/X/Arena_01.tdt", "Boss Arena"));
        }
        finally
        {
            File.Delete(log);
        }
    }

    /// <summary>
    /// Every boss picture the sheet has for a G4 area is reached by the rules alone.
    /// </summary>
    /// <remarks>
    /// THE MEASUREMENT THIS FEATURE RESTS ON. Eight of the sheet's boss families are named for
    /// an area rather than for a boss - G4_3_1_BossActive, G4_4_2BossActive - and those areas
    /// are ids the tool already reads. So the deriving is not a hopeful guess: it is checked
    /// here against the shipped name table, one area at a time, with no curated file involved.
    ///
    /// If the sheet ever gains or loses such a family this fails, which is the point - the
    /// rules and the art are supposed to keep agreeing.
    /// </remarks>
    [Fact]
    public void TheDerivedNamesFindTheAreaBossesTheSheetCarries()
    {
        HashSet<string> sheet = SheetNames();
        string[] areas = ["G4_2_2", "G4_3_1", "G4_3_2", "G4_4_2", "G4_7", "G4_8b", "G4_10", "G4_11_2"];

        foreach (string area in areas)
        {
            bool found = BossIcons.Empty
                .Candidates(area, "Metadata/Terrain/Islands/Tiles/Somewhere/BossArena_01.tdt")
                .Any(family => sheet.Contains(BossIcons.Named(family, cleared: false)));

            Assert.True(found, $"{area}: no candidate matched a cell name in the sheet");
        }
    }

    /// <summary>
    /// What the export writes: the entry, beside the pictures, in one click.
    /// </summary>
    /// <remarks>
    /// THE ROUND TRIP IS THE TEST. Writing is only useful if the next start-up reads back what
    /// was written, and this file is loaded by a source-generated deserialiser under AOT - so
    /// an entry that serialises to a shape the reader ignores would look like a write that
    /// silently did nothing. Every case here writes, loads the file again, and asks the new
    /// table rather than the one that did the writing.
    /// </remarks>
    [Fact]
    public void AnEntryWrittenFromThePaneIsThereOnTheNextRun()
    {
        string path = Path.Combine(Path.GetTempPath(), $"boss-icons-{Guid.NewGuid():N}.json");
        File.WriteAllText(
            path,
            """{ "comment": ["the block that documents the format"], "areas": {}, "tiles": {} }""");

        try
        {
            BossIcons icons = BossIcons.Load(path);
            Assert.True(icons.Remember(
                ["MapGrimhaven", "MapEpitaph"],
                "Metadata/Terrain/Maps/Grimhaven/Feature/BossArena_01.tdt",
                "WifeMonsterMap",
                "Saphira, The Dread Consort",
                out string said));
            Assert.Contains("boss-icons", said, StringComparison.OrdinalIgnoreCase);

            BossIcons again = BossIcons.Load(path);

            // One boss, two maps, one family - the case that made the name a key of its own.
            Assert.Equal("WifeMonsterMap", again.FamilyFor("MapGrimhaven"));
            Assert.Equal("WifeMonsterMap", again.FamilyFor("MapEpitaph"));
            Assert.Equal("Saphira, The Dread Consort", again.NameOf("WifeMonsterMap"));

            // The tile is the more precise key and is offered first, extension or not.
            Assert.Equal(
                "WifeMonsterMap",
                again.Candidates("MapSomewhereElse", "Metadata/Terrain/Maps/Grimhaven/Feature/BossArena_01.tdt")[0]);

            // And the file still explains itself, which it will not if the comment is dropped
            // the first time somebody follows the instructions in it.
            Assert.Contains("documents the format", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnEntryNeedsSomewhereToWriteAndSomethingToWrite()
    {
        // No file was ever loaded, so there is nowhere to put it. Refused rather than written
        // to a path this invented, which is how somebody's afternoon of entries goes missing.
        Assert.False(BossIcons.Empty.Remember(["MapBluff"], string.Empty, "SomeBoss", string.Empty, out string nowhere));
        Assert.Contains("boss-icons.json", nowhere, StringComparison.Ordinal);

        string path = Path.Combine(Path.GetTempPath(), $"boss-icons-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "areas": {}, "tiles": {} }""");
        try
        {
            BossIcons icons = BossIcons.Load(path);

            // Nothing to key it by, and no picture name to file it under.
            Assert.False(icons.Remember([], string.Empty, "SomeBoss", "A Boss", out _));
            Assert.False(icons.Remember(["MapBluff"], string.Empty, "  ", "A Boss", out _));
            Assert.Equal(0, icons.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TickingAMapOffIsRememberedAndUndoingItPutsItBack()
    {
        string path = Path.Combine(Path.GetTempPath(), $"boss-icons-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "areas": {}, "tiles": {} }""");
        try
        {
            BossIcons icons = BossIcons.Load(path);
            Assert.True(icons.Skip("MapPrecursorTowerDesert", skip: true, out _));
            Assert.True(BossIcons.Load(path).Skipped("MapPrecursorTowerDesert"));

            Assert.True(icons.Skip("MapPrecursorTowerDesert", skip: false, out _));
            Assert.False(BossIcons.Load(path).Skipped("MapPrecursorTowerDesert"));

            // And writing an entry for a map that was ticked off takes the tick back: the
            // entry is the answer to the question the tick was silencing.
            Assert.True(icons.Skip("MapBluff", skip: true, out _));
            Assert.True(icons.Remember(["MapBluff"], string.Empty, "BluffBoss", "Someone", out _));
            Assert.False(BossIcons.Load(path).Skipped("MapBluff"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AWrittenEntryMovesTheRevision()
    {
        string path = Path.Combine(Path.GetTempPath(), $"boss-icons-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "areas": {}, "tiles": {} }""");
        try
        {
            BossIcons icons = BossIcons.Load(path);
            int was = icons.Revision;

            Assert.True(icons.Remember(["MapBluff"], string.Empty, "BluffBoss", "Someone", out _));

            // What the marker in front of the person writing the entry watches, so that it
            // stops saying "Boss Arena" without them leaving the arena first.
            Assert.NotEqual(was, icons.Revision);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheCollectedArenasCanBeReadBackPerArea()
    {
        string log = Path.Combine(Path.GetTempPath(), $"arenas-{Guid.NewGuid():N}.tsv");
        try
        {
            BossIcons icons = BossIcons.Load(
                Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.json"), log);

            icons.NoteMissing("MapBluff", "Metadata/Terrain/Maps/Bluff/Arena_01.tdt", "Boss Arena");
            icons.NoteMissing("MapMesa", "Metadata/Terrain/Maps/Mesa/Arena_02.tdt", "Boss Arena");

            // What fills the tile field for a map somebody played an hour ago, from the
            // hideout, with nothing of that area left in memory.
            Assert.Equal(["Metadata/Terrain/Maps/Bluff/Arena_01.tdt"], icons.Unnamed("MapBluff"));
            Assert.Empty(icons.Unnamed("MapAugury"));
        }
        finally
        {
            File.Delete(log);
        }
    }

    /// <summary>
    /// An arena the sheet knows nothing about resolves to nothing rather than to something.
    /// </summary>
    /// <remarks>
    /// The other half of the rule, and the more important one: a derived candidate is only
    /// ever accepted because the sheet carries a picture under exactly that name, so an
    /// ordinary arena in an ordinary map has to come back empty and keep the boss shape. A
    /// rule that resolved this one would be drawing somebody else's boss.
    /// </remarks>
    [Fact]
    public void AnArenaWithNoPictureMatchesNothing()
    {
        HashSet<string> sheet = SheetNames();

        foreach (string family in BossIcons.Empty.Candidates(
            "MapAugury", "Metadata/Terrain/Maps/Augury/Features/Arena_Combined_03.tdt"))
        {
            Assert.DoesNotContain(BossIcons.Named(family, cleared: false), sheet);
            Assert.DoesNotContain(BossIcons.Named(family, cleared: true), sheet);
            Assert.DoesNotContain(family, sheet);
        }
    }
}
