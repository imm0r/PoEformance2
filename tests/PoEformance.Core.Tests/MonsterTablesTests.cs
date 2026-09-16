using PoEformance.Features;
using PoEformance.Game.Entities;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// Assembling the monster table out of the install's own eight .dat files.
/// </summary>
/// <remarks>
/// WHAT THESE CAN AND CANNOT SETTLE, said plainly because the difference is the whole point.
/// Nothing in this repository has a PoE2 install to parse, so no test here can put the reader in
/// front of the real MonsterVarieties.datc64. What they DO is drive it through the SHIPPED column
/// layouts - data/monster-tables.json, the same file the tool loads - so every offset under test is
/// the offset the tool will use, and a column that moves in that file fails a test here rather than
/// producing a silently wrong monster on somebody's screen.
///
/// WHAT CHECKS THE LAYOUT ITSELF is the file's own row size at runtime - a .dat declares it by
/// where it puts its separator, LoadedTable.Agrees - and after that the shipped export, which
/// reached the same 2733 monsters by a completely different route. MonsterTables.Say reports both.
///
/// THE AWKWARD CASES ARE HERE ON PURPOSE, which is what a made-up table is for: a null reference
/// whose second word is zero, a "Nothing" filler modifier, an empty array, a sentinel row that is
/// not a monster, and a float column that would read as a bit pattern. Every one of them is a way
/// this reader can be wrong while looking right.
/// </remarks>
public class MonsterTablesTests
{
    /// <summary>The vendored layouts, so the offsets under test are the shipped ones.</summary>
    private static QuestTableLayouts Layouts()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "monster-tables.json")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Assert.IsType<QuestTableLayouts>(
            QuestTableLayouts.Load(Path.Combine(dir!.FullName, "data", "monster-tables.json")));
    }

    /// <summary>Where a column sits, refusing rather than defaulting when the layout has lost it.</summary>
    private static int At(QuestTableLayouts layouts, string table, string column)
    {
        int at = layouts.OffsetOf(table, column);
        Assert.True(at >= 0, $"the vendored layout has no {table}.{column}");
        return at;
    }

    /// <summary>
    /// An install holding all eight tables, with two monsters in it.
    /// </summary>
    /// <remarks>
    /// Row 0 is "Any", the table's own sentinel - the one Id that is not a metadata path. Row 1 is
    /// a monster with everything filled, and row 2 is one with nothing filled, which is where the
    /// null reference and the empty array live.
    /// </remarks>
    private static GameFiles Install(QuestTableLayouts layouts)
    {
        var monsters = new FakeDat(3, layouts.RowSizeOf("MonsterVarieties"));
        var types = new FakeDat(2, layouts.RowSizeOf("MonsterTypes"));
        var resistances = new FakeDat(2, layouts.RowSizeOf("MonsterResistances"));
        var blood = new FakeDat(2, layouts.RowSizeOf("BloodTypes"));
        var tags = new FakeDat(3, layouts.RowSizeOf("Tags"));
        var effects = new FakeDat(2, layouts.RowSizeOf("GrantedEffects"));
        var mods = new FakeDat(3, layouts.RowSizeOf("Mods"));
        var stats = new FakeDat(2, layouts.RowSizeOf("Stats"));

        monsters
            .Text(0, At(layouts, "MonsterVarieties", "Id"), "Any")
            .Text(1, At(layouts, "MonsterVarieties", "Id"), "Metadata/Monsters/Test/Shieldbearer")
            .Text(1, At(layouts, "MonsterVarieties", "Name"), "Test Shieldbearer")
            .Text(1, At(layouts, "MonsterVarieties", "BaseMonsterTypeIndex"), "Metadata/Monsters/Test/Base")
            .Text(1, At(layouts, "MonsterVarieties", "Stance"), "stance2")
            .Reference(1, At(layouts, "MonsterVarieties", "MonsterType"), 1)
            .Reference(1, At(layouts, "MonsterVarieties", "BloodType"), 1)
            .Reference(1, At(layouts, "MonsterVarieties", "Questflag"), 5210)
            .I32(1, At(layouts, "MonsterVarieties", "MovementSpeed"), 38)
            .I32(1, At(layouts, "MonsterVarieties", "ObjectSize"), 12)
            .I32(1, At(layouts, "MonsterVarieties", "ModelSizeMultiplier"), 110)
            .I32(1, At(layouts, "MonsterVarieties", "MinimumAttackDistance"), 2)
            .I32(1, At(layouts, "MonsterVarieties", "MaximumAttackDistance"), 9)
            .I32(1, At(layouts, "MonsterVarieties", "MinAgroRange"), 20)
            .I32(1, At(layouts, "MonsterVarieties", "MaxAgroRange"), 55)
            .I32(1, At(layouts, "MonsterVarieties", "ExperienceMultiplier"), 130)
            .I32(1, At(layouts, "MonsterVarieties", "DamageMultiplier"), 115)
            .I32(1, At(layouts, "MonsterVarieties", "LifeMultiplier"), 220)
            .I32(1, At(layouts, "MonsterVarieties", "AttackSpeed"), 1500)
            .F32(1, At(layouts, "MonsterVarieties", "AttackCrit"), 2f)
            .F32(1, At(layouts, "MonsterVarieties", "PoiseThreshold"), 0.065f)
            .Bool(1, At(layouts, "MonsterVarieties", "BossHealthBar"), true)
            .References(1, At(layouts, "MonsterVarieties", "Tags"), 0, 2)
            .References(1, At(layouts, "MonsterVarieties", "GrantedEffects"), 1)
            .References(1, At(layouts, "MonsterVarieties", "Mods"), 1)

            // The filler slot, which is what Mods2 is mostly full of: 29% of every modifier
            // reference in the real table points at the one row called "Nothing".
            .References(1, At(layouts, "MonsterVarieties", "Mods2"), 2)
            .Texts(1, At(layouts, "MonsterVarieties", "InheritsFrom"), "Metadata/Monsters/AbyssMonsterBase")

            // And the bare one. Its type, blood and quest columns are NULLS rather than zeros,
            // which is the case that reads as row zero when nothing refuses it first.
            .Text(2, At(layouts, "MonsterVarieties", "Id"), "Metadata/Monsters/Test/Bare")
            .Text(2, At(layouts, "MonsterVarieties", "Name"), "[ANY MONSTER]")
            .Text(2, At(layouts, "MonsterVarieties", "BaseMonsterTypeIndex"), "Metadata/Monsters/Test/Bare")
            .Null(2, At(layouts, "MonsterVarieties", "MonsterType"))
            .Null(2, At(layouts, "MonsterVarieties", "BloodType"))
            .Null(2, At(layouts, "MonsterVarieties", "Questflag"));

        types
            .Text(0, At(layouts, "MonsterTypes", "Id"), "NoTypeAtAll")
            .Text(1, At(layouts, "MonsterTypes", "Id"), "TestShieldbearer")
            .I32(1, At(layouts, "MonsterTypes", "Armour"), 150)
            .I32(1, At(layouts, "MonsterTypes", "Evasion"), 60)
            .I32(1, At(layouts, "MonsterTypes", "EnergyShieldFromLife"), 25)
            .I32(1, At(layouts, "MonsterTypes", "DamageSpread"), 20)
            .Bool(1, At(layouts, "MonsterTypes", "IsSummoned"), true)

            // Row 0 is the table's own "no profile" row and is dropped, so only the 1 survives.
            .References(1, At(layouts, "MonsterTypes", "MonsterResistances"), 0, 1);

        resistances
            .Text(0, At(layouts, "MonsterResistances", "Id"), "NoResistance")
            .Text(1, At(layouts, "MonsterResistances", "Id"), "MajorFireResist");

        blood
            .Text(0, At(layouts, "BloodTypes", "Id"), "Blood")
            .Text(1, At(layouts, "BloodTypes", "Id"), "RotBlood");

        tags
            .Text(0, At(layouts, "Tags", "Id"), "undead")
            .Text(1, At(layouts, "Tags", "Id"), "not_referenced")
            .Text(2, At(layouts, "Tags", "Id"), "zombie");

        effects
            .Text(0, At(layouts, "GrantedEffects", "Id"), "NotReferenced")
            .Text(1, At(layouts, "GrantedEffects", "Id"), "MeleeAtAnimationSpeed");

        stats
            .Text(0, At(layouts, "Stats", "Id"), "monster_base_block_%")
            .Text(1, At(layouts, "Stats", "Id"), "base_block_%_damage_taken");

        mods
            .Text(0, At(layouts, "Mods", "Id"), "NotReferenced")
            .Text(1, At(layouts, "Mods", "Id"), "MonsterAttackBlock30Bypass15")
            .Reference(1, At(layouts, "Mods", "Stat1"), 0)
            .I32(1, At(layouts, "Mods", "Stat1Value"), 30)
            .I32(1, At(layouts, "Mods", "Stat1Value") + 4, 30)
            .Reference(1, At(layouts, "Mods", "Stat2"), 1)
            .I32(1, At(layouts, "Mods", "Stat2Value"), 15)
            .I32(1, At(layouts, "Mods", "Stat2Value") + 4, 15)

            // The upper half of the slots, which scripts/monster-varieties.py never read - this
            // route does, and a stat put here is the proof.
            .Reference(1, At(layouts, "Mods", "Stat8"), 1)
            .I32(1, At(layouts, "Mods", "Stat8Value"), -20)
            .I32(1, At(layouts, "Mods", "Stat8Value") + 4, 20)
            .Text(2, At(layouts, "Mods", "Id"), MonsterTables.Filler);

        // Every slot this reader does not fill has to be a null rather than a zero, or a row of
        // zeros reads as a table full of references to row nought.
        for (var row = 0; row < 3; row++)
        {
            for (int slot = 1; slot <= MonsterTables.StatSlots; slot++)
            {
                if (row == 1 && slot is 1 or 2 or 8)
                {
                    continue;
                }

                mods.Null(row, At(layouts, "Mods", $"Stat{slot}"));
            }
        }

        GameFiles? files = FakeInstall.Of(
            ("data/monstervarieties.datc64", monsters.Bytes()),
            ("data/monstertypes.datc64", types.Bytes()),
            ("data/monsterresistances.datc64", resistances.Bytes()),
            ("data/bloodtypes.datc64", blood.Bytes()),
            ("data/tags.datc64", tags.Bytes()),
            ("data/grantedeffects.datc64", effects.Bytes()),
            ("data/mods.datc64", mods.Bytes()),
            ("data/stats.datc64", stats.Bytes()));

        Assert.NotNull(files);
        return files!;
    }

    [Fact]
    public void AMONSTERComesOutOfTheInstallWithEveryReferenceResolved()
    {
        QuestTableLayouts layouts = Layouts();
        MonsterTables read = MonsterTables.Read(Install(layouts), layouts, MonsterVarieties.Empty);

        Assert.True(read.FromGame, string.Join("\n", read.Say));

        MonsterVariety? one = read.Table.Find("Metadata/Monsters/Test/Shieldbearer");
        Assert.NotNull(one);
        Assert.Equal("Test Shieldbearer", one.Name);
        Assert.Equal("Metadata/Monsters/Test/Base", one.Base);
        Assert.Equal("stance2", one.Stance);
        Assert.True(one.Boss);

        // The numbers, in the order the layout puts them - an off-by-one anywhere in the column
        // list moves all of these at once, which is why they are checked as a block.
        Assert.Equal(38, one.Speed);
        Assert.Equal(12, one.Size);
        Assert.Equal(110, one.ModelSize);
        Assert.Equal(2, one.MinAttack);
        Assert.Equal(9, one.MaxAttack);
        Assert.Equal(20, one.MinAggro);
        Assert.Equal(55, one.MaxAggro);
        Assert.Equal(130, one.Xp);
        Assert.Equal(115, one.Damage);
        Assert.Equal(220, one.Life);
        Assert.Equal(1500, one.AttackSpeed);
        Assert.Equal(5210, one.Quest);

        // And the names every row number resolves to.
        Assert.Equal("TestShieldbearer", read.Table.Kind(one)?.Id);
        Assert.Equal("RotBlood", read.Table.BloodName(one));
        Assert.Equal(["undead", "zombie"], read.Table.TagsOf(one));
        Assert.Equal(["MeleeAtAnimationSpeed"], read.Table.Skills(one));
        Assert.Equal(["Metadata/Monsters/AbyssMonsterBase"], one.Inherits);
    }

    [Fact]
    public void ANDTheTwoFloatColumnsAreFloatsRatherThanBitPatterns()
    {
        // 0.065f IS 0x3D851EB8, so a float column read as an int comes back as 1032495800 - and
        // AttackCrit's 2 comes back as 1073741824. dat-schema calls PoiseThreshold an i32; the
        // values the export produced for it are 0.05, 0.055, 0.065 and 0.25, which no integer
        // column holds. The width is four bytes either way, so nothing about the row size says so.
        QuestTableLayouts layouts = Layouts();
        MonsterTables read = MonsterTables.Read(Install(layouts), layouts, MonsterVarieties.Empty);

        MonsterVariety? one = read.Table.Find("Metadata/Monsters/Test/Shieldbearer");
        Assert.NotNull(one);
        Assert.Equal(0.065, one.Poise, 5);

        // A KIND AND NOT A CHANCE - 0, 1 or 2 across the whole table - so it is the number it is.
        Assert.Equal(2, one.Crit);
    }

    [Fact]
    public void ANDAModifierArrivesWithItsStatsAndTheEmptySlotsGone()
    {
        QuestTableLayouts layouts = Layouts();
        MonsterTables read = MonsterTables.Read(Install(layouts), layouts, MonsterVarieties.Empty);

        MonsterVariety? one = read.Table.Find("Metadata/Monsters/Test/Shieldbearer");
        Assert.NotNull(one);

        // ONE modifier and not two: the monster carries the filler row in Mods2, and a table that
        // kept it would draw a blank line under every monster that has one.
        ModifierMeaning mod = Assert.Single(read.Table.Modifiers(one));
        Assert.Equal("MonsterAttackBlock30Bypass15", mod.Id);
        Assert.Null(one.Mods2);

        Assert.Equal(
            ["monster_base_block_% 30-30", "base_block_%_damage_taken 15-15", "base_block_%_damage_taken -20-20"],
            (mod.Stats ?? []).Select(stat => $"{stat.Stat} {stat.Min}-{stat.Max}"));

        // THE EIGHTH SLOT IS THE POINT OF THAT THIRD ENTRY. The export reads four; this reads
        // eight, so a modifier using the upper half arrives whole here and truncated there.
        Assert.Equal(3, mod.Stats?.Count);
    }

    [Fact]
    public void ANULLReferenceIsNotRowZero()
    {
        // THE FAILURE THIS EXISTS TO CATCH, and it is silent: a null reference is 0xFFFFFFFF in
        // its first word with the second left at zero, and RowIn takes whichever word is a valid
        // row. Row zero is a real monster type and a real blood type, so an unset column reads as
        // a monster that is confidently the wrong kind rather than as one with no kind at all.
        QuestTableLayouts layouts = Layouts();
        MonsterTables read = MonsterTables.Read(Install(layouts), layouts, MonsterVarieties.Empty);

        MonsterVariety? bare = read.Table.Find("Metadata/Monsters/Test/Bare");
        Assert.NotNull(bare);

        // MINUS ONE and not zero, because zero is a row: BloodTypes row 0 is "Blood" and 1092
        // monsters carry it, so "nothing" spelled as zero is the commonest real answer there is.
        Assert.Equal(-1, bare.Type);
        Assert.Equal(-1, bare.Blood);
        Assert.Equal(-1, bare.Quest);

        // Row zero of each of those tables exists and is named, so the check is that the monster
        // does not resolve to it - not that the name is missing from the table.
        Assert.Null(read.Table.Kind(bare));
        Assert.Equal(string.Empty, read.Table.BloodName(bare));
        Assert.Empty(read.Table.TagsOf(bare));

        // And a bracketed name is a placeholder the game shows to nobody, not a name.
        Assert.Null(bare.Name);

        // A base equal to the monster's own id says nothing, so it is not carried.
        Assert.Null(bare.Base);
    }

    [Fact]
    public void ANDTheSentinelRowIsNotAMonster()
    {
        // "Any" is the one Id in MonsterVarieties that is not a metadata path, so no entity could
        // ever carry it. Two monsters in, two monsters out.
        QuestTableLayouts layouts = Layouts();
        MonsterTables read = MonsterTables.Read(Install(layouts), layouts, MonsterVarieties.Empty);

        Assert.Equal(2, read.Table.Count);
        Assert.Null(read.Table.Find("Any"));
    }

    [Fact]
    public void ATABLEThatIsNotThereLeavesTheShippedExportStanding()
    {
        QuestTableLayouts layouts = Layouts();
        var shipped = MonsterVarieties.From(
            [new KeyValuePair<string, MonsterVariety>("Metadata/Monsters/Test/Shipped", new MonsterVariety("Shipped"))],
            new Dictionary<int, string>(),
            new Dictionary<int, ModifierMeaning>(),
            new Dictionary<int, string>(),
            new Dictionary<int, MonsterKind>(),
            new Dictionary<int, string>(),
            new Dictionary<int, string>(),
            "a test");

        MonsterTables read = MonsterTables.Read(FakeInstall.Of(), layouts, shipped);

        Assert.False(read.FromGame);
        Assert.Same(shipped, read.Table);
        Assert.Contains(read.Say, line => line.Contains("shipped table stands", StringComparison.Ordinal));
    }

    [Fact]
    public void ANDNoInstallIsNotACrash()
    {
        MonsterTables read = MonsterTables.Read(null, null, null);

        Assert.False(read.FromGame);
        Assert.Equal(0, read.Table.Count);
        Assert.Contains(read.Say, line => line.Contains("no install", StringComparison.Ordinal));
    }

    [Fact]
    public void THEEXPORTIsTheOtherCheckAndTheReportSaysHowFarTheyAgree()
    {
        // TWO ROUTES TO THE SAME 2733 MONSTERS - a Python tool over hand-exported CSVs and this
        // reader over the install's own bytes - and agreeing is the strongest evidence available
        // that either is right. Reported rather than asserted, because this runs mid-league on
        // somebody's machine and an install that has moved on from the export is expected.
        QuestTableLayouts layouts = Layouts();
        MonsterTables installed = MonsterTables.Read(Install(layouts), layouts, MonsterVarieties.Empty);

        var shipped = MonsterVarieties.From(
            [
                new("Metadata/Monsters/Test/Shieldbearer", new MonsterVariety("Test Shieldbearer", Type: 1, Effects: [1])),
                new("Metadata/Monsters/Test/Bare", new MonsterVariety("A NAME THE INSTALL DOES NOT GIVE IT")),
                new("Metadata/Monsters/Test/Gone", new MonsterVariety("only in the export")),
            ],
            new Dictionary<int, string>(),
            new Dictionary<int, ModifierMeaning>(),
            new Dictionary<int, string>(),
            new Dictionary<int, MonsterKind>(),
            new Dictionary<int, string>(),
            new Dictionary<int, string>(),
            "a test");

        string report = string.Join("\n", MonsterTables.Against(installed.Table, shipped));

        Assert.Contains("2 of 2 paths in both", report, StringComparison.Ordinal);
        Assert.Contains("1 only in the export", report, StringComparison.Ordinal);
        Assert.Contains("1 of the shared ones differ", report, StringComparison.Ordinal);
        Assert.Contains("Metadata/Monsters/Test/Bare", report, StringComparison.Ordinal);
    }
}
