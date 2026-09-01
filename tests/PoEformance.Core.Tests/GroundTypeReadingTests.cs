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

    /// <summary>Every ground effect in the capture, once, with the type rows it ever showed.</summary>
    private static Dictionary<uint, HashSet<int>> TypesPerEntity(out int readings, out int withType)
    {
        using var replay = ReplayMemoryReader.Load(File.OpenRead(Fixture("session-2026-08-sweep.rec")));
        var world = new WorldReader(replay, RealSessionTests.Schema());
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        var perEntity = new Dictionary<uint, HashSet<int>>();
        readings = 0;
        withType = 0;

        for (uint frame = 0; frame < replay.FrameCount; frame += 5)
        {
            replay.Seek(frame);
            foreach (WorldEntity entity in world.Read(gameStates).Entities.Where(e => e.IsGroundEffect))
            {
                readings++;
                if (entity.GroundType is not { } row)
                {
                    continue;
                }

                withType++;
                if (!perEntity.TryGetValue(entity.Id, out HashSet<int>? seen))
                {
                    perEntity[entity.Id] = seen = [];
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
        Dictionary<uint, HashSet<int>> perEntity = TypesPerEntity(out int readings, out int withType);

        Assert.True(readings > 5000, $"only {readings} ground readings in the capture");
        Assert.Equal(readings, withType);
        Assert.NotEmpty(perEntity);
    }

    [Fact]
    public void TheTypeRowHoldsStillForAnEntitysWholeLife()
    {
        // THE PROPERTY THAT EARNED IT THE NAME, and the check that separates a type from running
        // state. Two fields in this component separate a hideout from a map more cleanly than
        // this one does - +0x64 and +0x68 - and both move WITHIN a single entity's life, which
        // is what disqualified them. A type does not move. If this ever fails, the offset is
        // reading something dynamic and the name TypeRow is a lie.
        Dictionary<uint, HashSet<int>> perEntity = TypesPerEntity(out _, out _);

        Assert.All(perEntity, entity =>
            Assert.True(entity.Value.Count == 1,
                $"entity {entity.Key} showed {entity.Value.Count} type rows: "
                + string.Join(", ", entity.Value.Order())));
    }

    [Fact]
    public void TheRowsAreSmallEnoughToIndexATable()
    {
        // Not a claim that these ARE the right rows - only the table can say that. It is the
        // claim that makes looking them up worth doing at all: a row index is small, and a slot
        // holding millions would mean the offset landed on something that is not one.
        Dictionary<uint, HashSet<int>> perEntity = TypesPerEntity(out _, out _);
        List<int> rows = [.. perEntity.Values.SelectMany(v => v).Distinct().Order()];

        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.InRange(row, 0, 4096));
    }

    // ── The table, through its failure modes ───────────────────────────────

    [Fact]
    public void WithNoGameInstallTheTableSaysSoAndNamesNothing()
    {
        // The ordinary case on a machine that is only replaying a recording, and the one that
        // must not throw: the ring still draws, it just goes back to showing the entity path.
        GroundEffectTypeTable table = GroundEffectTypeTable.Load(null, DataFile("ground-tables.json"));

        Assert.Equal(0, table.Rows);
        Assert.Null(table.Find(17));
        Assert.Contains("install", table.Why, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AMissingLayoutIsReportedRatherThanGuessedAround()
    {
        GroundEffectTypeTable table = GroundEffectTypeTable.Load(null, "/nowhere/ground-tables.json");

        Assert.Equal(0, table.Rows);
        Assert.NotEmpty(table.Why);
    }

    [Fact]
    public void AnUnknownRowNamesNothingRatherThanTheWrongGround()
    {
        // OUT OF RANGE IS A REAL ANSWER. It means the offset is wrong, the table moved, or the
        // value was never a row index - and naming a kind of ground confidently in any of those
        // cases is worse than saying nothing, because somebody would believe it.
        GroundEffectTypeTable table = GroundEffectTypeTable.Load(null, DataFile("ground-tables.json"));

        Assert.Null(table.Find(null));
        Assert.Null(table.Find(999_999));
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
