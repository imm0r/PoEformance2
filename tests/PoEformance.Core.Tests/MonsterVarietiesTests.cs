using PoEformance.Features;
using PoEformance.Game.Entities;

namespace PoEformance.Core.Tests;

/// <summary>
/// The monster table, against the captures and against itself.
/// </summary>
/// <remarks>
/// WHY THE CAPTURES ARE THE REFERENCE HERE TOO. The table's whole value rests on one claim -
/// that its Id column IS the path an entity carries - and that claim is not visible from either
/// side alone. A table full of plausible paths and a game full of plausible paths can disagree
/// completely while both look right. The captured areas are the only place in this repository
/// where real PoE2 paths and this table meet.
/// </remarks>
public sealed class MonsterVarietiesTests
{
    private static MonsterVarieties? _shipped;

    private static MonsterVarieties Shipped()
    {
        if (_shipped is not null)
        {
            return _shipped;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "monster-varieties.json")))
        {
            dir = dir.Parent;
        }

        _shipped = MonsterVarieties.Load(Path.Combine(dir!.FullName, "data", "monster-varieties.json"));
        return _shipped;
    }

    private static IReadOnlyList<string> Captured(string name)
    {
        foreach (string root in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var at = new DirectoryInfo(root);
            while (at is not null)
            {
                string candidate = Path.Combine(at.FullName, "fixtures", "preloads", name);
                if (File.Exists(candidate))
                {
                    return [.. File.ReadAllLines(candidate)
                        .Select(line => line.Trim())
                        .Where(line => line.Length > 0 && !line.StartsWith('#'))];
                }

                at = at.Parent;
            }
        }

        throw new FileNotFoundException($"capture {name} not found");
    }

    /// <summary>
    /// The paths in a capture that could be an entity rather than one of its files.
    /// </summary>
    /// <remarks>
    /// A preload list is a FILE list, so most of what sits under Metadata/Monsters/ is art: .ao
    /// models, .act animations, attachments/ folders. Measured over all of them the table looks
    /// like it resolves a fifth of the game; measured over the paths that name a thing rather
    /// than a file, it resolves nearly all of it. Getting that population wrong is the easiest
    /// way to talk yourself out of a join that works.
    /// </remarks>
    private static IEnumerable<string> Identities(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            foreach (string path in PreloadAlerts.Names(line))
            {
                if (!path.StartsWith("Metadata/Monsters/", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("/attachments/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string last = path[(path.LastIndexOf('/') + 1)..];
                if (!last.Contains('.', StringComparison.Ordinal))
                {
                    yield return path;
                }
            }
        }
    }

    [Fact]
    public void THETABLEKnowsWhatARealAreaReallyLoaded()
    {
        MonsterVarieties table = Shipped();
        Assert.True(table.Count > 2000, $"only {table.Count} monsters - is the table there?");

        string[] identities = [.. Identities(Captured("map-rich.txt")).Distinct(StringComparer.OrdinalIgnoreCase)];
        Assert.True(identities.Length > 50, $"only {identities.Length} candidate paths in the capture");

        int known = identities.Count(path => table.Find(path) is not null);

        // Measured at 87% over all 21 captures. The floor is set below that rather than at it:
        // the paths that miss are spawners, arena props and objects/ folders - things under
        // Metadata/Monsters/ that are not monsters - and their share moves with the area.
        Assert.True(
            known * 100 / identities.Length >= 70,
            $"only {known} of {identities.Length} paths resolved - has the Id column stopped being the entity path?");
    }

    [Fact]
    public void AHideoutIsNotFullOfMonsters()
    {
        // The other half of the previous test, and the reason it is not vacuous. A lookup that
        // answered for everything would pass the 70% floor trivially; a hideout carries almost
        // no monster identities at all, so it separates "the join works" from "Find says yes".
        MonsterVarieties table = Shipped();

        int hideout = Identities(Captured("hideout.txt"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(path => table.Find(path) is not null);

        int map = Identities(Captured("map-rich.txt"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(path => table.Find(path) is not null);

        Assert.True(map > hideout * 3, $"a map should carry far more monsters than a hideout; {map} vs {hideout}");
    }

    [Fact]
    public void ANonMonsterGetsNoAnswer()
    {
        // What stops the browser from captioning a chest. These are real paths out of the
        // captures, all under Metadata/ and none of them a monster variety.
        MonsterVarieties table = Shipped();

        Assert.Null(table.Find("Metadata/Chests/StrongBoxes/Strongbox"));
        Assert.Null(table.Find("Metadata/Terrain/Leagues/Breach/brequelportal.arm"));
        Assert.Null(table.Find("Metadata/Monsters/Anomalies/CircleOfPower"));
        Assert.Null(table.Find(""));
        Assert.Null(table.Find(null));
    }

    [Fact]
    public void AVariantIsTheSameMonster()
    {
        // From the reference tool rather than from the captures, which contain no @variant path
        // at all - a preload list holds files, and the suffix belongs to live entities. So this
        // pins the rule against a monster the table really has, in both spellings.
        MonsterVarieties table = Shipped();

        string known = "Metadata/Monsters/Zombies/Farmer/FarmerZombieMedium";
        Assert.NotNull(table.Find(known));

        Assert.NotNull(table.Find(known + "@SomeVariant"));
        Assert.Same(table.Find(known), table.Find(known + "@SomeVariant"));

        // And the spelling nobody would type on purpose but the game sometimes writes.
        Assert.NotNull(table.Find(known.Replace('/', '\\')));
    }

    [Fact]
    public void THEQUESTFLAGSAreBossesAndFitTheTableTheyIndex()
    {
        // THE CHECK THAT WOULD CATCH A WRONG COLUMN. Questflag holds a row number into
        // QuestFlags.dat, which this project measured against a real install at 5717 rows. A
        // value above that would mean the column indexes something else entirely - and a
        // browser resolving it through QuestWatch.FlagId would then show a flag belonging to a
        // different quest, confidently and wrongly.
        MonsterVarieties table = Shipped();

        var carrying = new List<MonsterVariety>();
        foreach (string path in Boss)
        {
            MonsterVariety? one = table.Find(path);
            Assert.NotNull(one);
            Assert.True(one.Quest > 0, $"{path} should carry a quest flag");
            carrying.Add(one);
        }

        // 5717 is what QuestFlags.dat measured at - see data/quest-tables.json's own note.
        Assert.All(carrying, one => Assert.InRange(one.Quest, 1, 5717));

        // And they are bosses, which is the whole reason the flag is interesting: the column
        // says "this kill is a quest step", so it has no business on a trash mob.
        Assert.All(carrying, one => Assert.True(one.Boss, $"{one.Name} carries a quest flag but no boss bar"));

        // A monster with no quest flag is the normal case, and must not read as row 0.
        MonsterVariety? ordinary = table.Find("Metadata/Monsters/Zombies/Farmer/FarmerZombieMedium");
        Assert.NotNull(ordinary);
        Assert.Equal(0, ordinary.Quest);
    }

    /// <summary>Named campaign bosses, which is what the quest flag turned out to mark.</summary>
    private static readonly string[] Boss =
    [
        "Metadata/Monsters/Baron/BaronBossCorruptedWolfForm",
        "Metadata/Monsters/MudBurrower/MudBurrowerHeadBoss",
        "Metadata/Monsters/IgnagdukBogWitch/IgnagdukBogWitch",
        "Metadata/Monsters/YamaBoss/YamaBoss",
    ];

    [Fact]
    public void ABOSSCarriesFarMoreSkillsThanATrashMob()
    {
        // Why the skill COUNT is worth drawing even with no names for them. If this ever
        // stopped holding, the effects column would have become something other than a per
        // monster skill list, and the count would be decoration.
        MonsterVarieties table = Shipped();

        MonsterVariety? boss = table.Find("Metadata/Monsters/YamaBoss/YamaBoss");
        MonsterVariety? mob = table.Find("Metadata/Monsters/Zombies/Farmer/FarmerZombieMedium");

        Assert.NotNull(boss);
        Assert.NotNull(mob);
        Assert.True(
            boss.SkillCount > mob.SkillCount * 3,
            $"a boss should be far busier than a zombie; {boss.SkillCount} vs {mob.SkillCount}");
    }

    [Theory]
    [InlineData("Metadata/Monsters/YamaBoss/YamaBoss", "Yama")]
    [InlineData("Metadata/Monsters/IgnagdukBogWitch/IgnagdukBogWitch", "Ignagduk")]
    [InlineData("Metadata/Monsters/MudBurrower/MudBurrowerHeadBoss", "MudBurrower")]
    public void ABOSSSSkillsAreNamedAfterTheBoss(string path, string word)
    {
        // THE CHECK THAT CATCHES AN OFF-BY-ONE, which is the only way this resolution can be
        // wrong while looking entirely right. Shifted by one row every monster still gets a
        // plausible skill - a zombie's "MeleeAtAnimationSpeed" becomes "MeleeAtAnimationSpeed2"
        // and nothing about that reads as broken.
        //
        // What a shift cannot survive is the game naming a boss's skills after the boss.
        // Ignagduk really does have GTIgnagdukBoneWall1, 2 and 3; shift the table and she gets
        // GTBogShamanBoneWall1 and Yama gets DTTPaleFishman.
        MonsterVarieties table = Shipped();
        MonsterVariety? one = table.Find(path);

        Assert.NotNull(one);
        Assert.True(one.SkillCount > 3, $"{path} should be a boss with several skills");

        string[] named = [.. table.Skills(one)];
        int hits = named.Count(name => name.Contains(word, StringComparison.OrdinalIgnoreCase));

        Assert.True(
            hits * 2 > named.Length,
            $"only {hits} of {named.Length} skills mention {word}: {string.Join(", ", named)}");
    }

    [Fact]
    public void ATRASHMOBGetsTheSkillItReallyHas()
    {
        // The other end of the same measurement, and the one an off-by-one makes look fine. It
        // is here so the boss check above is not the only thing standing between a shifted
        // table and a shipped one.
        MonsterVarieties table = Shipped();
        MonsterVariety? zombie = table.Find("Metadata/Monsters/Zombies/Farmer/FarmerZombieMedium");

        Assert.NotNull(zombie);
        Assert.Equal(["MeleeAtAnimationSpeed"], table.Skills(zombie));
    }

    [Fact]
    public void AMODIFIERSaysWhatItSets()
    {
        // Modifiers are the half that answers "why is this one different", and they are only
        // worth drawing if they arrive with their stats attached rather than as a bare id.
        MonsterVarieties table = Shipped();
        MonsterVariety? yama = table.Find("Metadata/Monsters/YamaBoss/YamaBoss");

        Assert.NotNull(yama);

        ModifierMeaning[] carried = [.. table.Modifiers(yama)];
        Assert.NotEmpty(carried);
        Assert.All(carried, one => Assert.NotEmpty(one.Id));

        // The boss modifier, and the stat that says it is one.
        ModifierMeaning? boss = carried.FirstOrDefault(
            one => one.Stats?.Any(stat => stat.Stat == "i_am_boss_of_tier") == true);
        Assert.NotNull(boss);
    }

    [Fact]
    public void THEEMPTYMODIFIERSLOTSAreGone()
    {
        // Mods2 is a fixed-width array whose filler row is literally called "Nothing", and 29%
        // of every modifier reference in the table pointed at it. Left in, a monster with one
        // real modifier draws five blank lines under it.
        MonsterVarieties table = Shipped();

        MonsterVariety? zombie = table.Find("Metadata/Monsters/Zombies/Farmer/FarmerZombieMedium");
        Assert.NotNull(zombie);

        ModifierMeaning[] carried = [.. table.Modifiers(zombie)];
        Assert.NotEmpty(carried);
        Assert.All(carried, one => Assert.NotEqual("Nothing", one.Id));

        // And the one it really has, with the value the game gives it.
        ModifierMeaning? stance = carried.FirstOrDefault(one => one.Id == "StanceMovementSpeed300");
        Assert.NotNull(stance);
        Assert.Equal("300", stance.Stats?.Single(s => s.Stat.Contains("movement_speed", StringComparison.Ordinal)).Range);
    }

    [Fact]
    public void ANUNNAMEDSKILLStaysVisibleAsItsRow()
    {
        // A row with no name must not vanish. A monster with sixty-seven skills and forty names
        // is a table that needs refreshing; one that quietly showed forty would look complete.
        MonsterVarieties none = MonsterVarieties.Empty;
        var one = new MonsterVariety(Effects: [7, 11]);

        Assert.Equal(["#7", "#11"], none.Skills(one));
        Assert.Equal(0, none.NamedSkills);
    }

    [Fact]
    public void THETABLESaysWhenItWasBuilt()
    {
        // A monster table is a snapshot of one patch, and the way a stale one fails is that new
        // monsters have no name - which reads as a lookup bug rather than an old file.
        Assert.NotEmpty(Shipped().Generated);
        Assert.StartsWith("20", Shipped().Generated, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingTableIsNotACrash()
    {
        // The browser has to keep working without it, because it always did.
        MonsterVarieties none = MonsterVarieties.Load(Path.Combine(Path.GetTempPath(), $"no-{Guid.NewGuid():N}.json"));

        Assert.Equal(0, none.Count);
        Assert.Null(none.Find("Metadata/Monsters/Zombies/Farmer/FarmerZombieMedium"));
        Assert.Empty(none.Generated);
    }
}
