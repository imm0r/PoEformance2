using PoEformance.Core.Memory;
using PoEformance.Features;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The ground-effect type row, from memory through to the table that names it.
/// </summary>
/// <remarks>
/// TWO HALVES THAT CAN ONLY BE TESTED SEPARATELY HERE, and it is worth saying which is which so
/// nobody reads more confidence into this file than it carries.
///
/// The MEMORY half runs against the recorded session and is a real measurement: the value is
/// there, it survives the trip into a WorldSnapshot, and it holds still for an entity's whole
/// life. That last one is the property that made it the type candidate in the first place.
///
/// The TABLE half cannot run against the game's files at all - this suite runs on Linux with no
/// Path of Exile install - so what is tested is every way it can FAIL: no install, no layout, a
/// layout that does not parse, a row nobody asked about. Those are the paths a person actually
/// meets, and the one that must never throw or silently name the wrong ground. Whether row 17 is
/// really called what the game calls it is settled by running --groundtypes on a machine with
/// the game on it, and by nothing in here.
/// </remarks>
public class GroundTypeReadingTests
{
    private static string Fixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "tests", "fixtures", name);
    }

    private static string DataFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", name)))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "data", name);
    }

    /// <summary>
    /// The type rows each ground effect showed, in order, over one capture.
    /// </summary>
    /// <remarks>
    /// KEYED ON ADDRESS AS WELL AS ID. The game reuses entity ids, so an id alone would let two
    /// different patches of ground look like one patch changing its mind - which is exactly the
    /// claim these tests are about, and the one they must not be able to fake.
    ///
    /// Every frame, not every fifth. The finding below is about an entity's FIRST frame, and a
    /// sampled walk is precisely how it stayed hidden: the sweep capture was taken in a hideout
    /// whose decorations already existed, so no birth was ever recorded and the value looked
    /// constant on all 106 of them.
    /// </remarks>
    private static Dictionary<(uint Id, ulong Address), List<int>> TypesPerEntity(
        string fixture, out int readings, out int withType)
    {
        using var replay = ReplayMemoryReader.Load(File.OpenRead(Fixture(fixture)));
        var world = new WorldReader(replay, RealSessionTests.Schema());
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        var perEntity = new Dictionary<(uint, ulong), List<int>>();
        readings = 0;
        withType = 0;

        for (uint frame = 0; frame < replay.FrameCount; frame++)
        {
            replay.Seek(frame);
            foreach (WorldEntity entity in world.Read(gameStates).Entities
                         .Where(e => e.IsGroundEffect && !e.IsRemembered))
            {
                readings++;
                if (entity.GroundType is not { } row)
                {
                    continue;
                }

                withType++;
                if (!perEntity.TryGetValue((entity.Id, entity.Address), out List<int>? seen))
                {
                    perEntity[(entity.Id, entity.Address)] = seen = [];
                }

                seen.Add(row);
            }
        }

        return perEntity;
    }

    [Fact]
    public void TheTypeRowReachesTheSnapshotOnEveryGroundEffect()
    {
        // The half a decode usually dies in: the offset is right in a diagnostic and never
        // reaches a snapshot, which from the screen looks exactly like an offset that is wrong.
        Dictionary<(uint, ulong), List<int>> perEntity =
            TypesPerEntity("session-2026-08-sweep.rec", out int readings, out int withType);

        Assert.True(readings > 5000, $"only {readings} ground readings in the capture");
        Assert.Equal(readings, withType);
        Assert.NotEmpty(perEntity);
    }

    /// <summary>
    /// The type settles one frame after the entity appears, and never moves again.
    /// </summary>
    /// <remarks>
    /// THIS TEST USED TO CLAIM MORE AND WAS PASSING ON AN ACCIDENT. It asserted the row was
    /// constant across an entity's whole life, which held on 106 of 106 entities in the sweep
    /// capture - because that capture was taken in a hideout whose decorations already existed
    /// when recording started, so not one BIRTH was ever recorded.
    ///
    /// A map capture caught two, and both behave the same way: entity #693 read 16 on its first
    /// listed frame and 18 for the whole rest of its life; #719 read 10, then 18. Same address
    /// throughout, so this is not id reuse. The likeliest reading is a pooled component cell
    /// still holding its previous occupant's value until the server fills it a tick later - the
    /// values are plausible OTHER type rows rather than garbage - but what is measured is only
    /// that the first frame differs and everything after it does not.
    ///
    /// The property is still the one that separates a type from running state: +0x64 and +0x68
    /// move continuously throughout a life. This moves once, at birth, and then holds.
    /// </remarks>
    [Theory]
    [InlineData("session-2026-08-sweep.rec")]
    [InlineData("session-2026-09-groundtypes.rec")]
    public void TheTypeRowSettlesAfterTheFirstFrameAndThenHoldsStill(string fixture)
    {
        Dictionary<(uint Id, ulong Address), List<int>> perEntity =
            TypesPerEntity(fixture, out _, out _);

        Assert.NotEmpty(perEntity);
        Assert.All(perEntity, entity =>
        {
            List<int> settled = [.. entity.Value.Skip(1)];
            Assert.True(settled.Distinct().Count() <= 1,
                $"entity #{entity.Key.Id} at {entity.Key.Address:X} kept changing after its first "
                + $"frame: {string.Join(", ", entity.Value.Take(12))}");
        });
    }

    [Fact]
    public void ABirthFrameIsWhereTheRowIsNotYetTrustworthy()
    {
        // The other half, stated as its own test so the finding cannot quietly evaporate: in the
        // map capture NO entity is constant across its whole life, and every one of them is
        // constant after its first frame. If a future build initialises the slot earlier this
        // goes green in the wrong direction and should be re-read, not deleted.
        Dictionary<(uint Id, ulong Address), List<int>> perEntity =
            TypesPerEntity("session-2026-09-groundtypes.rec", out _, out _);

        Assert.NotEmpty(perEntity);
        Assert.All(perEntity, entity => Assert.True(entity.Value.Count > 1));
        Assert.DoesNotContain(perEntity, entity => entity.Value.Distinct().Count() == 1);
    }

    [Fact]
    public void TheRowsAreSmallEnoughToIndexATable()
    {
        // Not a claim that these ARE the right rows - only the table can say that. It is the
        // claim that makes looking them up worth doing at all: a row index is small, and a slot
        // holding millions would mean the offset landed on something that is not one. The real
        // table has 53 rows, observed on an install.
        Dictionary<(uint, ulong), List<int>> perEntity =
            TypesPerEntity("session-2026-09-groundtypes.rec", out _, out _);
        List<int> rows = [.. perEntity.Values.SelectMany(v => v).Distinct().Order()];

        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.InRange(row, 0, 4096));
    }

    [Fact]
    public void AMapUsesDifferentGroundTypesFromAHideout()
    {
        // What makes the field worth reading at all. The entity path is identical on every
        // ground effect in the game, so if the type row were identical too there would be
        // nothing to tell a burning patch from a decoration. It is not: the hideout capture
        // shows 12, 17 and 20, and the map capture settles on 18 with short countdowns.
        Dictionary<(uint, ulong), List<int>> hideout =
            TypesPerEntity("session-2026-08-sweep.rec", out _, out _);
        Dictionary<(uint, ulong), List<int>> map =
            TypesPerEntity("session-2026-09-groundtypes.rec", out _, out _);

        // Settled values only - a birth frame says nothing about what a thing IS.
        var inHideout = hideout.Values.SelectMany(v => v.Skip(1)).ToHashSet();
        var inMap = map.Values.SelectMany(v => v.Skip(1)).ToHashSet();

        Assert.NotEmpty(inHideout);
        Assert.NotEmpty(inMap);
        Assert.DoesNotContain(inMap, row => inHideout.Contains(row));
    }

    // ── The table, through its failure modes ───────────────────────────────

    private static GroundEffectTypeTable Shipped()
        => GroundEffectTypeTable.Load(
            null, DataFile("ground-tables.json"), DataFile("ground-effect-types.json"));

    [Fact]
    public void WithNoInstallTheVendoredTableStillNamesTheGround()
    {
        // The ordinary case on a machine that is only replaying a recording - which is every
        // machine these tests run on. Before the vendored copy existed this could only be tested
        // for NOT THROWING, so the whole resolution was untested. Now it is the tested path.
        GroundEffectTypeTable table = Shipped();

        Assert.Equal(53, table.Rows);
        Assert.Contains("vendored", table.Why, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheRowsTheCapturesShowedAreTheGroundTheyName()
    {
        // THE PAYOFF, and the check that the offset points where it is claimed to. These rows
        // come out of the two recordings; the names come out of the game's own table. If +0x48
        // were reading something else, these would be arbitrary rows and the names would drift
        // to nonsense the next time the table changed.
        GroundEffectTypeTable table = Shipped();

        Assert.Equal("Spores", table.Find(12)?.Id);
        Assert.Equal("OrionMeteor", table.Find(17)?.Id);
        Assert.Equal("CrownOfThorns", table.Find(18)?.Id);
        Assert.Equal("Profane", table.Find(20)?.Id);

        // And the buff is what a player actually sees written on their own screen, which is why
        // the label leads with it rather than with the internal Id.
        Assert.Equal("Sacred Ashes", table.Find(18)?.Buff);
        Assert.Equal("CrownOfThorns - Sacred Ashes", table.Find(18)?.Describe);
    }

    [Fact]
    public void EveryRowIsARealEffectKindThatAppliesABuff()
    {
        // The finding that reversed an earlier conclusion in this file's history: there is no
        // decorative row. Every one of the 53 applies at least one buff, and the names are
        // Ignited Ground, Chilled Ground, Shocked Ground, Caustic Ground. So carrying a
        // GroundEffect component means the game considers this one of its ground-effect kinds -
        // it was briefly documented as meaning the opposite.
        GroundEffectTypeTable table = Shipped();

        Assert.All(table.All, type => Assert.True(type.Buffs >= 1, $"row {type.Row} ({type.Id}) applies none"));
        Assert.Contains(table.All, t => t.Id == "IgnitedGround");
        Assert.Contains(table.All, t => t.Id == "ChilledGround");
        Assert.Contains(table.All, t => t.Id == "ShockedGround");
    }

    [Fact]
    public void AMissingLayoutStillLeavesTheVendoredNames()
    {
        // The layout only matters for reading the INSTALL. Losing it must not cost the names.
        GroundEffectTypeTable table = GroundEffectTypeTable.Load(
            null, "/nowhere/ground-tables.json", DataFile("ground-effect-types.json"));

        Assert.Equal(53, table.Rows);
        Assert.NotEmpty(table.Why);
    }

    [Fact]
    public void WithNeitherSourceItNamesNothingAndSaysSo()
    {
        GroundEffectTypeTable table = GroundEffectTypeTable.Load(null, "/nowhere/a.json", "/nowhere/b.json");

        Assert.Equal(0, table.Rows);
        Assert.Null(table.Find(17));
        Assert.NotEmpty(table.Why);
    }

    [Fact]
    public void AnUnknownRowNamesNothingRatherThanTheWrongGround()
    {
        // OUT OF RANGE IS A REAL ANSWER. It means the offset is wrong, the table moved, or the
        // value was never a row index - and naming a kind of ground confidently in any of those
        // cases is worse than saying nothing, because somebody would believe it.
        GroundEffectTypeTable table = Shipped();

        Assert.Null(table.Find(null));
        Assert.Null(table.Find(999_999));
        Assert.Null(table.Find(53));
    }

    [Fact]
    public void TheShippedLayoutComputesTheRowSizeTheSchemaImplies()
    {
        // The one thing about the TABLE that can be checked without the game: that the vendored
        // column list adds up to what the DAT schema describes - string 8, i32 4, f32 4, and
        // three foreign rows at 16 each. If a width is ever corrected, this catches the layout
        // silently shifting every column after it.
        QuestTableLayouts? layouts = QuestTableLayouts.Load(DataFile("ground-tables.json"));

        Assert.NotNull(layouts);
        Assert.Equal(64, layouts.RowSizeOf(GroundEffectTypeTable.Table));
        Assert.Equal(0, layouts.OffsetOf(GroundEffectTypeTable.Table, "Id"));
        Assert.Equal(16, layouts.OffsetOf(GroundEffectTypeTable.Table, "Stat"));
        Assert.Equal(32, layouts.OffsetOf(GroundEffectTypeTable.Table, "BuffDefinition1"));
        Assert.Equal(48, layouts.OffsetOf(GroundEffectTypeTable.Table, "BuffDefinition2"));
    }

    [Fact]
    public void ATypeWithNoNameStillShowsItsRow()
    {
        // A caption that renders as an empty string is a label that vanishes, which reads as
        // "nothing here" rather than "this row has no Id". The row number is always worth more
        // than nothing at all.
        Assert.Equal("type 17", new GroundEffectType(17, string.Empty, 0, false).Caption);
        Assert.Equal("BurningGround", new GroundEffectType(17, "BurningGround", 1, true).Caption);
    }
}
