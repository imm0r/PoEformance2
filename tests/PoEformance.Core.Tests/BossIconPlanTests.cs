using PoEformance.Features;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The list of endgame maps whose boss still needs a picture.
/// </summary>
/// <remarks>
/// THE ONE MEASUREMENT WORTH KEEPING IS AT THE BOTTOM, against the two shipped files: of the
/// 173 maps the game says the atlas can hold, how many resolve a boss picture from their name
/// alone. The answer is none, which is why the list exists - and if a later release of the
/// sheet changes that, the number in the remark there should change with it rather than the
/// claim quietly becoming false.
/// </remarks>
public class BossIconPlanTests
{
    /// <summary>A map table with the given ids, written where the test can reach it.</summary>
    private static AtlasMapNames Maps(params (string Id, string Name, string Tag)[] maps)
    {
        string path = Path.Combine(Path.GetTempPath(), $"atlas-maps-{Guid.NewGuid():N}.json");
        IEnumerable<string> rows = maps.Select(map => map.Tag.Length > 0
            ? $"\"{map.Id}\": {{ \"name\": \"{map.Name}\", \"tags\": [\"{map.Tag}\"] }}"
            : $"\"{map.Id}\": {{ \"name\": \"{map.Name}\" }}");

        File.WriteAllText(path, "{ \"maps\": {" + string.Join(',', rows) + "} }");
        try
        {
            return AtlasMapNames.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>An empty curated table that can be written to, and its file.</summary>
    private static (BossIcons Icons, string Path) Table()
    {
        string path = Path.Combine(Path.GetTempPath(), $"boss-icons-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "areas": {}, "tiles": {} }""");
        return (BossIcons.Load(path, Path.Combine(Path.GetTempPath(), $"arenas-{Guid.NewGuid():N}.tsv")), path);
    }

    /// <summary>The shipped files, found the way the other tests find them.</summary>
    private static string Beside(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine([dir.FullName, .. parts])))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine([dir!.FullName, .. parts]);
    }

    [Fact]
    public void AMapWithNothingWrittenDownIsWorkToDo()
    {
        (BossIcons icons, string path) = Table();
        try
        {
            List<BossIconTask> rows = BossIconPlan.Of(
                Maps(("MapBluff", "Bluff", string.Empty)), icons, _ => false);

            BossIconTask row = Assert.Single(rows);
            Assert.Equal(BossIconState.Open, row.State);
            Assert.Equal("Bluff", row.Name);
            Assert.Empty(row.Boss);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ThreeStatesSeparateTheThreeThingsLeftToDo()
    {
        (BossIcons icons, string path) = Table();
        try
        {
            icons.Remember(["MapGrimhaven"], string.Empty, "WifeMonsterMap", "Saphira", out _);
            icons.Remember(["MapBluff"], string.Empty, "BluffBoss", "Someone", out _);
            icons.Skip("MapLostTowers", skip: true, out _);

            // The sheet carries one of the two families. That is the whole difference between
            // "done" and "the art still has to go into icons.png".
            List<BossIconTask> rows = BossIconPlan.Of(
                Maps(
                    ("MapGrimhaven", "Grimhaven", string.Empty),
                    ("MapBluff", "Bluff", string.Empty),
                    ("MapLostTowers", "Lost Towers", "tower"),
                    ("MapAugury", "Augury", string.Empty)),
                icons,
                name => name == "BluffBossActive");

            Assert.Equal(BossIconState.Done, rows.Single(row => row.Id == "MapBluff").State);
            Assert.Equal(BossIconState.Waiting, rows.Single(row => row.Id == "MapGrimhaven").State);
            Assert.Equal(BossIconState.Skipped, rows.Single(row => row.Id == "MapLostTowers").State);
            Assert.Equal(BossIconState.Open, rows.Single(row => row.Id == "MapAugury").State);

            // The name is carried whether or not the picture is, which is what lets a marker
            // say whose arena it is before the art has been pasted in.
            Assert.Equal("Saphira", rows.Single(row => row.Id == "MapGrimhaven").Boss);

            (int open, int waiting, int done, int skipped) = BossIconPlan.Count(rows);
            Assert.Equal((1, 1, 1, 1), (open, waiting, done, skipped));

            // Work first, finished last: the list is a queue rather than a report.
            Assert.Equal(BossIconState.Open, rows[0].State);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnAreaWhoseOwnIdNamesItsPictureNeedsNoEntry()
    {
        (BossIcons icons, string path) = Table();
        try
        {
            // The eight G4_* families, which resolve with nothing written down - the same rule
            // the marker uses, checked here through the plan.
            List<BossIconTask> rows = BossIconPlan.Of(
                Maps(("G4_3_1", "The Reliquary", string.Empty)),
                icons,
                name => name == "G4_3_1_BossActive");

            Assert.Equal(BossIconState.Done, rows[0].State);
            Assert.Equal("G4_3_1_Boss", rows[0].Family);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheKindsWithNoBossAreQuietRatherThanDropped()
    {
        (BossIcons icons, string path) = Table();
        try
        {
            List<BossIconTask> rows = BossIconPlan.Of(
                Maps(
                    ("MapHideoutCanal_Claimable", "Canal Hideout", "hideout"),
                    ("MapPrecursorTowerDesert", "Sandspit", "tower"),
                    ("MapBluff", "Bluff", string.Empty)),
                icons,
                _ => false);

            // Listed - a rule that DROPPED them would drop a new league's maps too - and the
            // hideout marked so the list can hide it until somebody asks.
            //
            // THE TOWER IS NOT QUIET ANY MORE, and that is the measurement correcting a guess
            // rather than a preference: WorldAreas gives every Precursor tower two Reactor
            // Guardians, so "a tower has no boss" was simply false. What makes a row go away
            // is now the game saying there is nothing there - see the skip cases above.
            Assert.Equal(3, rows.Count);
            Assert.Equal(1, rows.Count(BossIconPlan.IsQuiet));
            Assert.True(BossIconPlan.IsQuiet(rows.Single(row => row.Id == "MapHideoutCanal_Claimable")));
            Assert.False(BossIconPlan.IsQuiet(rows.Single(row => row.Id == "MapPrecursorTowerDesert")));
            Assert.False(BossIconPlan.IsQuiet(rows.Single(row => row.Id == "MapBluff")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// What the shipped files add up to: the whole atlas, split into work and not-work.
    /// </summary>
    /// <remarks>
    /// THE MEASUREMENT THE FEATURE RESTS ON, against the three files the tool actually ships.
    /// The sheet's 27 boss families are named for campaign arenas and act bosses -
    /// GrimTangleBoss, IsleOfKinBoss, the G4_* eight - and every atlas map is named
    /// MapSomething, so NOT ONE of the 173 resolves a picture. What the game does supply is
    /// WHO stands there: 125 of them name a boss, 48 name none, and those 125 hold 104
    /// distinct monsters. So the work is 104 models rather than 173 maps, and that ratio is
    /// the whole argument for reading the column.
    ///
    /// Asserted as exact numbers because each one is a claim about the shipped data: art added
    /// to the sheet, or a patch that moves the column, should fail this and be written down
    /// rather than pass quietly.
    /// </remarks>
    [Fact]
    public void TheShippedFilesSplitTheAtlasIntoWorkAndNotWork()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadLines(Beside("assets", "icon-names.tsv")))
        {
            int tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab > 0 && !line.StartsWith('#'))
            {
                names.Add(line[(tab + 1)..].Trim());
            }
        }

        Assert.True(names.Count > 500, $"only {names.Count} cell names read - has the table moved?");

        AtlasMapNames maps = AtlasMapNames.Load(Beside("data", "atlas-maps.json"));
        Assert.Equal(173, maps.Count);

        BossIcons icons = BossIcons.Load(Beside("data", "boss-icons.json"));
        icons.Bosses = AreaBosses.Load(Beside("data", "area-bosses.json"));

        List<BossIconTask> rows = BossIconPlan.Of(maps, icons, names.Contains);

        (int open, int waiting, int done, int skipped) = BossIconPlan.Count(rows);
        Assert.Equal(0, done);
        Assert.Equal(0, waiting);
        Assert.Equal(48, skipped);
        Assert.Equal(125, open);

        // NINETY MODELS FOR A HUNDRED AND TWENTY-FIVE MAPS, and the gap is the point. The 125
        // list 104 distinct monsters between them, but a marker wears one picture, so what
        // has to be posed is the 90 distinct FIRST bosses - and 31 of those stand in more than
        // one map, covering 66 of the 125. That is the trap the column closes: filling this in
        // by hand means meeting the same boss again in another map, 31 times, with nothing in
        // either map to warn you.
        var families = rows
            .Where(row => row.State == BossIconState.Open)
            .Select(row => row.Family)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(90, families.Count);

        // And every one of those rows knows what to pose, by the name the game shows - and
        // carries the path the Monster Book is opened at, which is what makes the row a way in
        // rather than a name to retype into a table of 2733.
        Assert.Contains(rows, row => row.Id == "MapGrimhaven" && row.Family == "WifeMonsterMap");
        Assert.Contains(rows, row => row.Id == "MapEpitaph" && row.Family == "WifeMonsterMap");
        Assert.All(
            rows.Where(row => row.State == BossIconState.Open),
            row => Assert.NotEmpty(row.Path));
        Assert.Equal(
            "Metadata/Monsters/WifeMonster/WifeMonsterMap_",
            rows.Single(row => row.Id == "MapEpitaph").Path);
    }
}
