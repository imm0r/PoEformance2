using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;

namespace PoEformance.Core.Tests;

/// <summary>
/// A reported route to the quest flags, walked as far as the recordings can walk it.
/// </summary>
/// <remarks>
/// THE LEAD: <c>0x5A0, 0x60, 0x188, 0x248</c>, said to end at the start of a quest-flag vector.
/// It arrived as four bare numbers with no root, and the first of them names the root: 0x5A0 is
/// <c>AreaInstance.PlayerInfo</c>, whose VALUE is already <c>ServerDataPtr</c> - so the chain
/// runs AreaInstance -> ServerData -> +0x60 -> +0x188 -> +0x248, and the offsets are applied in
/// the order they were given.
///
/// WHAT THE RECORDINGS SETTLE, which is three hops of four. ServerData resolves in every
/// fixture; +0x60 resolves in four of them and lands on a polymorphic object with five vtables
/// at its head; +0x188 resolves in one and lands on an ordinary heap object. The fourth hop is
/// beyond every recording this project holds - nothing has ever read within 0x1000 of it -
/// which is not a surprise and is close to the point: the quest-flag hunt failed for six
/// sessions because nothing ever READ where the answer is, and a recording can only replay
/// reads that happened.
///
/// So this test does NOT claim the lead is right. It pins down the part that can be checked
/// offline, so that if a schema or reader change breaks the route to ServerData the failure is
/// here rather than in a live session that is hard to arrange. The last hop needs the game.
/// </remarks>
public class QuestFlagChainTests
{
    /// <summary>The reported chain, from the static the tool pattern-scans.</summary>
    /// <remarks>
    /// GameStates -> GameState+0x88 (States[4]) -> InGameState+0x290 (AreaInstanceData)
    /// -> AreaInstance+0x5A0 (PlayerInfo, whose value is ServerData) -> +0x60 -> +0x188 -> +0x248.
    /// </remarks>
    private const string Chain = "GameStates,88,290,5A0,60,188,248";

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

    private static (ReplayMemoryReader Reader, Dictionary<string, ulong> Statics) Load(string name)
    {
        var reader = ReplayMemoryReader.Load(File.OpenRead(Fixture(name)));
        return (reader, reader.ResolvedStatics.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void AStaticCanBeNamedInsteadOfHavingItsAddressPastedIn()
    {
        // The point of naming it: an RVA is only right for the build it was read on, and the
        // statics are pattern-scanned on every attach.
        Assert.True(PointerPath.TryParse(Chain, out PointerPath? path, out string error, ["GameStates"]));
        Assert.Equal(string.Empty, error);
        Assert.Equal("GameStates", path!.StaticName);
        Assert.Equal([0x88, 0x290, 0x5A0, 0x60, 0x188, 0x248], path.Offsets);
    }

    [Fact]
    public void AnUnknownStaticFailsAtResolveWithItsNameRatherThanAtParse()
    {
        (ReplayMemoryReader reader, _) = Load("session-2026-08-areamarkers.rec");
        using (reader)
        {
            PointerPath.TryParse("NoSuchStatic,10", out PointerPath? path, out _);

            PathResolution at = path!.Resolve(reader, new Dictionary<string, ulong>());

            Assert.False(at.Ok);
            Assert.Contains("NoSuchStatic", at.Hops[0].Label, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("session-2026-08-areamarkers.rec")]
    [InlineData("session-2026-08-questflags.rec")]
    [InlineData("session-2026-08-questflags-hunt.rec")]
    [InlineData("session-2026-08-map.rec")]
    public void TheChainReachesServerDataAndOneHopPastIt(string fixture)
    {
        (ReplayMemoryReader reader, Dictionary<string, ulong> statics) = Load(fixture);
        using (reader)
        {
            PointerPath.TryParse(Chain, out PointerPath? path, out _, statics.Keys);
            PathResolution at = path!.Resolve(reader, statics);

            // Hop 0 is the static itself, so hop N is offset N-1: ..., 4 = +0x5A0 (ServerData),
            // 5 = +0x60. Both must have READ something for the route to be real.
            Assert.True(at.Hops.Count > 5, $"only {at.Hops.Count} hops resolved");
            Assert.All(at.Hops.Take(6), hop => Assert.True(hop.Read, $"{hop.Label} did not read"));

            ulong serverData = at.Hops[4].Pointee;
            Assert.NotEqual(0UL, serverData);

            // ServerData is a big page-aligned allocation in every fixture. Its HEAD is only in
            // one of them - the tool reads the fields it wants, not the object - which is the
            // same reason the last hop of this chain is nowhere.
            Assert.Equal(0UL, serverData & 0xFFF);
        }
    }

    [Fact]
    public void TheHopThatOneRecordingReachesLandsOnAnObject()
    {
        // areamarkers is the only fixture that read far enough for +0x188 to resolve.
        (ReplayMemoryReader reader, Dictionary<string, ulong> statics) = Load("session-2026-08-areamarkers.rec");
        using (reader)
        {
            PointerPath.TryParse(Chain, out PointerPath? path, out _, statics.Keys);
            PathResolution at = path!.Resolve(reader, statics);

            // ServerData's head is vtables, which is what says the +0x5A0 field is the struct
            // itself rather than a pointer to follow twice - the mistake the schema records
            // against LocalPlayerStruct.
            Assert.True(reader.TryRead(at.Hops[4].Pointee, out ulong vtable));
            Assert.InRange(vtable, reader.ModuleBase, reader.ModuleBase + reader.ModuleSize);

            Assert.True(at.Hops[6].Read, "the +0x188 hop did not read");
            Assert.NotEqual(0UL, at.Hops[6].Pointee);

            // And the last hop is where every recording stops. Stated as an assertion so that a
            // future recording which DOES cover it fails here and gets this comment read.
            Assert.False(
                reader.TryRead(at.Target, out ulong _),
                "a recording now covers the final hop - walk it and settle the lead");
        }
    }
}
