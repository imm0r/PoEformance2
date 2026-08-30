using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Diagnostics;

namespace PoEformance.Core.Tests;

/// <summary>
/// The question <c>DeployedEntitiesProbe</c> was built to ask, answered by the game.
/// </summary>
/// <remarks>
/// SETTLED 2026-08-29, on a recording the owner made with a totem out - the one thing that was
/// missing, and the reason the field sat unverified for so long. Over 1208 frames:
///
/// - 329 frames decoded entries the ENTITY MAP KNOWS, and every single one of them at 0xC28.
///   Not one at 0xC18 or 0xC38.
/// - At 0xC18 - the offset the schema used to carry - <c>begin</c> reads 0. It is not an empty
///   vector there, it is not a vector at all.
/// - The 0x18 stride decodes cleanly: one deployed entity, one element, no remainder.
///
/// WHY THE OTHER 878 FRAMES DO NOT COUNT AGAINST IT, and this is worth being explicit about
/// rather than quietly filtering: they read the same single entry at 0xC28 whose id the entity
/// map no longer holds. A totem that has expired leaves the vector listing an entity the area
/// has already dropped, so those frames are the reading going stale rather than the offset being
/// wrong. The probe reports them as UNSETTLED, which is the honest answer for one frame taken
/// alone - the session is what settles it.
///
/// WHAT IS PROVEN IS THE OFFSET AND THE STRIDE, and no more. EntityId is checked against the
/// entity map, which is the game confirming it; SkillsDatId, DeployedObjectType and Counter are
/// decoded at their documented places and NOTHING here validates them - the sample's SkillsDatId
/// read -2063138751, which is not obviously a dat row id. Their layout comes from GameHelper2's
/// DeployedEntityStructure and stays unverified until something needs them.
///
/// THE OLD NOTE THIS REPLACES said "Empty (begin=end) with no minions/totems out - that is not a
/// drift". It could never have caught the drift it was dismissing, because a 0x10-short read
/// produces that same emptiness forever. The recording behind this file is what it always needed.
/// </remarks>
public class DeployedEntitiesConfirmedTests
{
    private const string Fixture = "session-2026-08-deployed.rec";

    private static string DirectoryHolding(string child)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, child)))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }

    /// <summary>Every frame's verdict, replayed once.</summary>
    private static readonly Lazy<List<DeployedProbeResult>> Session = new(() =>
    {
        string path = Path.Combine(DirectoryHolding("tests"), "tests", "fixtures", Fixture);
        var replay = ReplayMemoryReader.Load(File.OpenRead(path));
        OffsetSchema schema = RealSessionTests.Schema();
        var probe = new DeployedEntitiesProbe(replay, schema);
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        var all = new List<DeployedProbeResult>();
        for (uint frame = 0; frame < replay.FrameCount; frame++)
        {
            replay.Seek(frame);
            all.Add(probe.Run(gameStates));
        }

        return all;
    });

    private static DeployedReading[] Winners =>
        [.. Session.Value.Select(r => r.Winner).Where(w => w is not null).Select(w => w!.Value)];

    /// <summary>The game confirms the schema's offset, hundreds of times.</summary>
    [Fact]
    public void TheGameSettlesItAt0xC28()
    {
        DeployedReading[] won = Winners;

        Assert.True(won.Length > 200, $"only {won.Length} confirming frames");
        Assert.All(won, w => Assert.Equal(0xC28, w.Offset));
        Assert.All(won, w => Assert.Equal(0x18, w.Stride));
    }

    /// <summary>
    /// A confirming frame decodes an entity the area really holds - which is the check.
    /// </summary>
    /// <remarks>
    /// Deliberately not "the count looked plausible". Both candidates sit a few bytes apart in
    /// one component, so both can produce a believable header; an id that names a living entity
    /// cannot be produced by luck. That is how 0xB08 and 0xB20 were settled too.
    /// </remarks>
    [Fact]
    public void AConfirmingFrameNamesAnEntityTheAreaHolds()
    {
        DeployedReading first = Winners[0];

        Assert.NotEmpty(first.Equals(default) ? [] : first.Entries);
        Assert.Equal(first.Entries.Count, first.Matched);
        Assert.All(first.Entries, e => Assert.True(e.InEntityMap));
        Assert.All(first.Entries, e => Assert.NotEqual(0u, e.EntityId));
    }

    /// <summary>The old offset is not an empty vector there - it is not a vector.</summary>
    /// <remarks>
    /// The stronger half of the result, and the one that retires the old note for good: 0xC18
    /// reads begin = 0. "Empty with nothing deployed" was never even the right description of
    /// what it was doing.
    /// </remarks>
    [Fact]
    public void TheOldOffsetNeverReadsAsAVector()
    {
        DeployedReading[] old =
        [
            .. Session.Value.SelectMany(r => r.Readings).Where(r => r.Offset == 0xC18),
        ];

        Assert.NotEmpty(old);
        Assert.DoesNotContain(old, r => r.Confirmed);
        Assert.All(old, r => Assert.True(r.Begin == 0 || !r.HeaderSane, $"begin was 0x{r.Begin:X}"));
    }

    /// <summary>Nor does the offset on the far side of it.</summary>
    [Fact]
    public void NorDoesTheOffsetAboveIt()
    {
        DeployedReading[] above =
        [
            .. Session.Value.SelectMany(r => r.Readings).Where(r => r.Offset == 0xC38),
        ];

        Assert.NotEmpty(above);
        Assert.DoesNotContain(above, r => r.Confirmed);
    }

    /// <summary>
    /// The recording carries the Actor tail, which no earlier fixture did.
    /// </summary>
    /// <remarks>
    /// The reason this could not be answered before: all 15 fixtures were checked and not one
    /// held a byte of 0xB00..0xC48, because nothing in the build ever read it. Merging the probe
    /// was the precondition for the measurement, not its conclusion.
    ///
    /// ONE FRAME of the 1208 reads as no-data, and it is bounded rather than excluded: the actor
    /// resolves and none of the three candidates comes back, which is one read landing across a
    /// moment the session was between states. A corpus that did not hold these bytes would show
    /// that on every frame, which is the failure this distinguishes from.
    /// </remarks>
    [Fact]
    public void ThisFixtureActuallyHoldsTheBytes()
    {
        int blank = Session.Value.Count(r => r.NoData);
        int readable = Session.Value.Count(r => r.Readings.Any(x => x.Readable));

        Assert.True(readable > 1000, $"only {readable} frames could be read");
        Assert.True(blank < 10, $"{blank} frames held no data at all");
    }
}
