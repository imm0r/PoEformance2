using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.Diagnostics;
using PoEformance.Game.Entities;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The hunt for the two candidates nothing in this tool read, and what one capture settled.
/// </summary>
/// <remarks>
/// Both questions were blocked by the same rule rather than by a hard offset: a replay only
/// serves reads that actually happened, so an offset no build touches is absent from every
/// session ever captured. The hunt existed to change that, and session-2026-08-hoverhunt.rec
/// is what it produced - 940 frames of deliberately hovering monsters, a chest, a checkpoint
/// and a ground item.
///
/// It answered ONE of them. The hovered-entity chain is real and is now read in production; the
/// boss byte is exactly as unproven as it was, because no unique monster was in the area, and
/// that is the case the tests below spend the most care on. An unread field returning zero
/// looks exactly like a field that means zero, and so does a flag on a screen with nothing to
/// set it - "the byte was zero on every monster, so 0x27 is wrong" is the sentence these tests
/// exist to keep out of a report that has not earned it.
/// </remarks>
public class HoverHuntTests
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

    private static List<HoverSample> Replay(string fixture, uint step = 5)
    {
        using var replay = ReplayMemoryReader.Load(File.OpenRead(Fixture(fixture)));
        OffsetSchema schema = RealSessionTests.Schema();
        var hunt = new HoverHunt(replay, schema);
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        var samples = new List<HoverSample>();
        for (uint frame = 0; frame < replay.FrameCount; frame += step)
        {
            replay.Seek(frame);
            if (hunt.SampleFrame(gameStates) is { } sample)
            {
                samples.Add(sample);
            }
        }

        return samples;
    }

    [Fact]
    public void ItSamplesASessionWithoutInventingAnything()
    {
        List<HoverSample> samples = Replay("session-2026-08-frustum.rec");
        Assert.NotEmpty(samples);

        // Not even the FIRST hop is in an ordinary session, which is worth pinning because the
        // obvious assumption is wrong: the chain walk reads InGameState at 0x290, 0x2F0 and
        // 0x368 by name, not as a block, so 0x300 is untouched like everything else. It shows
        // up only in the --questflags captures, which sweep the struct wholesale.
        //
        // This says something about the frustum capture rather than about the game, and it
        // will change the next time this fixture is re-recorded: the production reader added
        // with the confirmation reads the chain every frame, so a session made from now on
        // HAS the host. That is the point of adding it, and this test then belongs on an
        // older file.
        Assert.All(samples, s => Assert.Equal(0ul, s.Host));

        // And everything past it: the sub-object and the Monster component are separate
        // allocations nobody has read, so a replay cannot serve them, and the hunt must come
        // back empty-handed instead of reporting zeros as findings.
        Assert.All(samples, s => Assert.Equal(string.Empty, s.EntityPath));
        Assert.All(samples, s => Assert.Empty(s.BossBytes));

        // The one file that DOES hold the first hop, so "the chain resolves as far as the
        // bytes go" is demonstrated rather than assumed - and so a future change that stops
        // the host pointer resolving at all is caught.
        Assert.Contains(Replay("session-2026-08-areamarkers.rec"), s => s.Host != 0);
    }

    [Fact]
    public void OnAFileThatCannotAnswer_TheReportSaysSoRatherThanConcluding()
    {
        var text = new StringWriter();
        HoverHunt.Report(Replay("session-2026-08-frustum.rec"), text);
        string report = text.ToString();

        Assert.Contains("no monster carried a readable Monster component", report, StringComparison.Ordinal);

        // And it must not claim the chain named anything.
        Assert.DoesNotContain("DISTINCT ENTITIES", report, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptySessionIsReportedAsNoFrames()
    {
        var text = new StringWriter();
        HoverHunt.Report([], text);
        Assert.Contains("no frames", text.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The measurement that turned the chain from a copied offset into a confirmed one.
    /// </summary>
    /// <remarks>
    /// The claim is NOT "the slot holds a pointer" - a wrong offset into a live object holds
    /// pointers all day. It is that every value it takes is an address the GAME itself was
    /// listing in AwakeEntities on that same frame, which nothing but the hovered entity has a
    /// reason to be, and that it is empty most of the time, which rules out the other reading
    /// that would fit: a nearest-entity or last-targeted slot in an area with 96 monsters in it
    /// would essentially never be empty.
    /// </remarks>
    [Fact]
    public void TheHoveredEntitySlotOnlyEverNamesEntitiesTheGameIsListing()
    {
        using var replay = ReplayMemoryReader.Load(File.OpenRead(Fixture("session-2026-08-hoverhunt.rec")));
        OffsetSchema schema = RealSessionTests.Schema();
        var hunt = new HoverHunt(replay, schema);
        var hovered = new MouseOverReader(replay, schema);
        var entities = new EntityReader(replay, schema);
        var map = new EntityMapReader(replay, schema);
        ulong gameStates = replay.ResolvedStatics["GameStates"];
        int awake = schema.Structs["AreaInstance"].OffsetOf("AwakeEntities");

        int frames = 0, filled = 0, listed = 0, unlisted = 0;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var values = new List<ulong>();

        for (uint frame = 0; frame < replay.FrameCount; frame++)
        {
            replay.Seek(frame);
            GameChainAddresses chain = GameChain.Resolve(replay, schema, gameStates);
            if (chain.InGameState == 0 || chain.AreaInstance == 0)
            {
                continue;
            }

            frames++;

            // The production reader, not the hunt's own walk - so this tests what ships.
            ulong at = hovered.Read(chain.InGameState);
            values.Add(at);
            if (at == 0)
            {
                continue;
            }

            filled++;
            bool inList = map
                .ReadEntityPointers(chain.AreaInstance + (ulong)awake, 4096, true)
                .Any(e => e.Value == at);
            if (inList)
            {
                listed++;
            }
            else
            {
                unlisted++;
            }

            if (entities.ReadIdentity(at) is { } identity && identity.Path.Length > 0)
            {
                paths.Add(identity.Path);
            }
        }

        Assert.True(frames > 900, $"only {frames} frames resolved a chain");

        // Not one exception in either direction. A single unlisted value would mean the slot
        // holds something else that occasionally looks like an entity, which is the failure
        // this is here to catch.
        Assert.True(listed > 100, $"only {listed} filled readings to judge by");
        Assert.Equal(0, unlisted);
        Assert.Equal(filled, listed);

        // Empty most of the time: what a cursor over floor looks like, and what a
        // nearest-entity slot could not look like here.
        Assert.True(filled < frames / 2, $"{filled} of {frames} filled - too many for a hover");

        // And it MOVED. One entity for the whole session would be equally consistent with a
        // stale pointer nobody clears.
        int changes = values.Zip(values.Skip(1)).Count(p => p.First != p.Second);
        Assert.True(changes > 20, $"the slot changed only {changes} times");

        // Monsters are not special to it - it is whatever the game lets the cursor pick up.
        Assert.True(paths.Count >= 5, $"only {paths.Count} distinct entities named");
        Assert.Contains(paths, p => p.Contains("/Monsters/", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.Contains("/Chests/", StringComparison.Ordinal));
    }

    /// <summary>
    /// The capture that finally refuted the boss byte, and what made it decisive.
    /// </summary>
    /// <remarks>
    /// Not "more data" - the first capture had 14,462 readings and settled nothing. THE CASE
    /// THAT SEPARATES THE HYPOTHESES. session-2026-08-hoverboss.rec was recorded in front of
    /// Metadata/Monsters/MudBurrower/MudBurrowerHeadBossMAP2__@70: Unique rarity, the word Boss
    /// in its own metadata path, and Monster+0x27 reads zero on it across every frame it was
    /// listed. A flag clear on that monster is not a boss flag, and the schema field is gone.
    ///
    /// The offset is still read here, deliberately, so the next boss re-checks a refutation
    /// that rests on ONE map boss rather than starting the question over.
    /// </remarks>
    [Fact]
    public void OnAMapBoss_TheByteIsZeroThereToo_WhichRefutesIt()
    {
        List<HoverSample> samples = Replay("session-2026-08-hoverboss.rec");
        List<(string Path, ItemRarity Rarity, byte Flag)> bytes = [.. samples.SelectMany(s => s.BossBytes)];

        // The case the first capture lacked.
        List<(string Path, ItemRarity Rarity, byte Flag)> uniques =
            [.. bytes.Where(b => b.Rarity == ItemRarity.Unique)];
        Assert.True(uniques.Count > 20, $"only {uniques.Count} sightings of a unique monster");
        Assert.Contains(uniques, u => u.Path.Contains("Boss", StringComparison.Ordinal));

        // And the byte is clear on it. This is the whole refutation.
        Assert.All(uniques, u => Assert.Equal(0, u.Flag));
        Assert.All(bytes, b => Assert.Equal(0, b.Flag));

        var text = new StringWriter();
        HoverHunt.Report(samples, text);
        Assert.Contains("0x27 is not this flag", text.ToString(), StringComparison.Ordinal);

        // The schema must not carry it any more - a field nothing can justify is a trap for
        // whoever reads the struct next and assumes provenance means verified.
        Assert.Null(RealSessionTests.Schema().Structs["Monster"].Field("IsBoss"));
    }

    /// <summary>
    /// The companion slot, identified: a per-frame handle onto the entity +0xA8 already names.
    /// </summary>
    /// <remarks>
    /// Two claims, and the test is written so either can fail on its own. The object's +0x00 is
    /// ONE class across the session, and its +0x08 is the hovered entity plus a fixed 0x100 -
    /// for a monster and for a ground item alike. Together they say the slot carries nothing new,
    /// which is a real answer: it stops the next person hunting it a third time.
    ///
    /// What is NOT asserted is the rest of the 0x200 window, and that is deliberate. Those bytes
    /// are neighbouring allocations in the same small-object arena - several different vtables
    /// across the session where +0x00 has exactly one - and reading them as this object's fields
    /// is the Inventories mistake wearing a new hat.
    /// </remarks>
    [Fact]
    public void TheCompanionIsAPerFrameHandleOntoTheHoveredEntity()
    {
        List<HoverSample> samples = Replay("session-2026-08-hoverboss.rec", step: 1);
        List<HoverSample> known = [.. samples.Where(s => s.CompanionRead > 0 && s.Entity != 0)];

        Assert.True(known.Count > 100, $"only {known.Count} companion targets captured");

        // One class.
        Assert.Single(known.Select(s => s.CompanionVTable).Distinct());

        // And a payload at a fixed offset into the hovered entity, without exception.
        Assert.All(known, s => Assert.Equal(s.Entity + HoverHunt.CompanionPayloadIntoEntity, s.CompanionPayload));

        // Both kinds of hovered thing, so the relation is not a property of monsters.
        Assert.Contains(known, s => s.EntityPath.Contains("/Monsters/", StringComparison.Ordinal));
        Assert.Contains(known, s => s.EntityPath.Contains("WorldItem", StringComparison.Ordinal));

        // The address moved constantly while all of that held - which is what made it look like
        // a find in the first place, and is worth keeping in the record.
        Assert.True(known.Select(s => s.Companion).Distinct().Count() > known.Count / 2);

        var text = new StringWriter();
        HoverHunt.Report(samples, text);
        Assert.Contains("AS IDENTIFIED", text.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The boss byte was ASKED and not answered, and the report has to say which.
    /// </summary>
    /// <remarks>
    /// This is the test that nearly went the other way. The capture reads Monster+0x27 on every
    /// monster in the area - thousands of sightings, every one of them zero - and writing that
    /// up as "0x27 is not the boss flag" would have been wrong for a reason no amount of data
    /// fixes: there was no unique monster in the area, and zero on every non-unique is what a
    /// CORRECT boss flag reads. A hypothesis is only refuted by the case that separates it.
    /// </remarks>
    [Fact]
    public void TheBossByteWasReadThousandsOfTimesAndStillProvesNothing()
    {
        List<HoverSample> samples = Replay("session-2026-08-hoverhunt.rec");
        List<(string Path, ItemRarity Rarity, byte Flag)> bytes = [.. samples.SelectMany(s => s.BossBytes)];

        // The bytes are really there - the whole reason the capture was made. (Every fifth
        // frame here; the schema comment quotes 14,462 from a pass over every third.)
        Assert.True(bytes.Count > 8_000, $"only {bytes.Count} readings of Monster+0x27");
        Assert.Contains(bytes, b => b.Rarity == ItemRarity.Normal);
        Assert.Contains(bytes, b => b.Rarity == ItemRarity.Magic);
        Assert.Contains(bytes, b => b.Rarity == ItemRarity.Rare);

        // And the case that would have decided it is not among them.
        Assert.DoesNotContain(bytes, b => b.Rarity == ItemRarity.Unique);
        Assert.All(bytes, b => Assert.Equal(0, b.Flag));

        var text = new StringWriter();
        HoverHunt.Report(samples, text);
        string report = text.ToString();

        Assert.Contains("NO UNIQUE was in the list", report, StringComparison.Ordinal);
        Assert.Contains("was not asked", report, StringComparison.Ordinal);

        // The conclusion the data does not support, kept out by name.
        Assert.DoesNotContain("0x27 is not this flag", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// The second cursor-tracking slot: what the file settles, and where it stops.
    /// </summary>
    /// <remarks>
    /// `sub+0xC8` tracks the cursor exactly as `+0xA8` does and is not the entity. The offline
    /// half of that question was taken as far as it goes against this capture, and the answer is
    /// mostly NEGATIVE - which is worth pinning, because each negative removes a guess that
    /// would otherwise look reasonable to the next person: it is reallocated per FRAME rather
    /// than per hover, so it is not a tooltip record built when the hover starts.
    ///
    /// And then it stops, for the reason this whole file keeps running into: the pointer is in
    /// the recording, its TARGET is not, because nothing read it. The hunt follows it now; until
    /// a capture is made with that build, `CompanionRead` is 0 and the report has to say so
    /// rather than implying the bytes are there.
    /// </remarks>
    [Fact]
    public void TheCompanionSlotTracksTheCursorButItsTargetIsInNoRecording()
    {
        using var replay = ReplayMemoryReader.Load(File.OpenRead(Fixture("session-2026-08-hoverhunt.rec")));
        OffsetSchema schema = RealSessionTests.Schema();
        var hunt = new HoverHunt(replay, schema);
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        var samples = new List<HoverSample>();
        for (uint frame = 0; frame < replay.FrameCount; frame++)
        {
            replay.Seek(frame);
            if (hunt.SampleFrame(gameStates) is { } sample)
            {
                samples.Add(sample);
            }
        }

        List<HoverSample> set = [.. samples.Where(s => s.Companion != 0)];

        // It fills and empties with the hover, exactly like the confirmed slot - which is why it
        // cannot simply be ignored as noise in the window.
        Assert.Equal(samples.Count(s => s.Entity != 0), set.Count);
        Assert.All(samples.Where(s => s.Entity == 0), s => Assert.Equal(0ul, s.Companion));

        // A fresh address on nearly every frame. If this ever drops towards one value per
        // hovered entity, the per-frame reading is wrong and the tooltip guess is back.
        Assert.True(
            set.Select(s => s.Companion).Distinct().Count() > set.Count * 0.9,
            "the companion no longer looks reallocated per frame");

        // 16-byte aligned, every one.
        Assert.All(set, s => Assert.Equal(0ul, s.Companion % 0x10));

        // And the wall: no committed recording holds the target, so the hunt must report having
        // captured none of it rather than serving zeros as content.
        Assert.All(set, s => Assert.Equal(0, s.CompanionRead));

        var text = new StringWriter();
        HoverHunt.Report(samples, text);
        Assert.Contains("its target read on NO frame", text.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void OnTheCaptureThatAnsweredIt_TheReportSaysTheChainNamedEntities()
    {
        var text = new StringWriter();
        HoverHunt.Report(Replay("session-2026-08-hoverhunt.rec"), text);
        string report = text.ToString();

        Assert.Contains("DISTINCT ENTITIES", report, StringComparison.Ordinal);
        Assert.Contains("Metadata/", report, StringComparison.Ordinal);
    }
}
