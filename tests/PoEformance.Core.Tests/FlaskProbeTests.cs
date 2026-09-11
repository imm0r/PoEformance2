using System.Text;
using System.Text.Json.Nodes;
using PoEformance.Core.Schema;
using PoEformance.Game.Diagnostics;

namespace PoEformance.Core.Tests;

/// <summary>
/// The flask probe against a synthetic world, and against a schema missing a field.
/// </summary>
/// <remarks>
/// WHAT THESE EXIST FOR. The probe asked <c>ServerDataStructure</c> for <c>League</c>, which
/// lives on <c>ServerDataOffsets</c> - the outer struct, a trap the schema records on the
/// field itself and that StashReader and StashInspector both get right. So <c>--flasks</c>
/// died with an unhandled KeyNotFoundException after three lines, in the one tool somebody
/// reaches for when the belt is not reading. Nothing caught it because nothing ran the probe
/// without the game.
///
/// So the first test RUNS it - a world just real enough to reach the league line, against the
/// SHIPPED schema, which is what makes it catch a field moving again. The second pins the
/// guard: a missing field has to arrive as a printed finding, because a probe that dies of
/// the schema is useless exactly when the schema is the suspect.
/// </remarks>
public class FlaskProbeTests
{
    private const ulong GameStatesStatic = 0x1_000000;
    private const ulong GameStateAddr = 0x2_000000;
    private const ulong InGameStateAddr = 0x3_000000;
    private const ulong AreaInstanceAddr = 0x4_000000;
    private const ulong PlayerEntityAddr = 0x6_000000;
    private const ulong ServerDataAddr = 0x7_000000;
    private const ulong LeagueTextAddr = 0x8_000000;

    private static string SchemaPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "schema", "poe2.offsets.json")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "schema", "poe2.offsets.json");
    }

    private static OffsetSchema Shipped() => SchemaJson.Load(SchemaPath());

    /// <summary>
    /// A world in an area whose ServerData carries a league name.
    /// </summary>
    /// <remarks>
    /// The game-state field is deliberately left unplaced: an unreadable state resolves to
    /// <c>Unreadable</c>, which counts as in-game as long as a player entity resolved - and a
    /// player pointer is one placement rather than a whole entity.
    ///
    /// Nothing past ServerData is placed either. The inventory walk then finds nothing and
    /// says so, which is a perfectly good end for these tests: what is under test is that the
    /// probe GETS there.
    /// </remarks>
    private static FakeMemoryReader World(OffsetSchema schema, string league = "Standard")
    {
        var fake = new FakeMemoryReader();

        StructDef gs = schema.Structs["GameState"];
        StructDef igs = schema.Structs["InGameState"];
        StructDef ai = schema.Structs["AreaInstance"];
        StructDef lp = schema.Structs["LocalPlayerStruct"];

        fake.Place(GameStatesStatic, GameStateAddr);
        fake.Place(
            GameStateAddr + (ulong)gs.OffsetOf("States")
                + (ulong)(gs.Constants["InGameStateIndex"] * gs.Constants["StateEntrySize"]),
            InGameStateAddr);
        fake.Place(InGameStateAddr + (ulong)igs.OffsetOf("AreaInstanceData"), AreaInstanceAddr);

        // LocalPlayerStruct is INLINE in AreaInstance - its base is the address of the
        // PlayerInfo field, not the value stored there.
        ulong player = AreaInstanceAddr + (ulong)ai.OffsetOf("PlayerInfo");
        fake.Place(player + (ulong)lp.OffsetOf("LocalPlayerPtr"), PlayerEntityAddr);
        fake.Place(player + (ulong)lp.OffsetOf("ServerDataPtr"), ServerDataAddr);

        fake.PlaceStdWString(
            ServerDataAddr + (ulong)schema.Structs["ServerDataOffsets"].OffsetOf("League"),
            league,
            LeagueTextAddr);

        return fake;
    }

    private static string Run(OffsetSchema schema, FakeMemoryReader fake)
    {
        var output = new StringWriter();
        new FlaskProbe(fake, schema).Report(GameStatesStatic, output);
        return output.ToString();
    }

    [Fact]
    public void THEPROBEReadsTheLeagueOffTheStructThatHasIt()
    {
        OffsetSchema schema = Shipped();

        string report = Run(schema, World(schema));

        // The league line at all is the finding: reaching it means the lookup resolved.
        Assert.Contains("Standard", report, StringComparison.Ordinal);
        Assert.Contains("ServerData confirmed", report, StringComparison.Ordinal);

        // And it carried on past it. A probe that threw here printed three lines and stopped,
        // so "got further than ServerData" is the thing worth asserting.
        Assert.Contains("serverDataStruct", report, StringComparison.Ordinal);
    }

    [Fact]
    public void ANDTheOuterStructIsTheONLYOneThatHasIt()
    {
        // The half that says WHY the wrong lookup threw rather than reading a wrong number:
        // the field is not on the inner struct at all. If a schema edit ever puts a League on
        // both, a probe asking the wrong one would read plausible rubbish instead of failing,
        // and this is the test that would go red first.
        OffsetSchema schema = Shipped();

        Assert.NotNull(schema.Structs["ServerDataOffsets"].Field("League"));
        Assert.Null(schema.Structs["ServerDataStructure"].Field("League"));
    }

    [Fact]
    public void ANDAMissingFieldIsAFindingRatherThanACrash()
    {
        // A probe is what somebody runs when the schema is the suspect, so the schema must not
        // be what kills it. OffsetOf already throws with struct AND field named, which is
        // exactly the sentence worth printing - so the message survives and the process does.
        OffsetSchema shipped = Shipped();
        FakeMemoryReader fake = World(shipped);

        string report = Run(WithoutLeague(), fake);

        Assert.Contains("FAIL", report, StringComparison.Ordinal);
        Assert.Contains("ServerDataOffsets", report, StringComparison.Ordinal);
        Assert.Contains("League", report, StringComparison.Ordinal);
        Assert.Contains("poe2.offsets.json", report, StringComparison.Ordinal);
    }

    /// <summary>The shipped schema with ServerDataOffsets.League taken out.</summary>
    /// <remarks>
    /// Edited as JSON rather than as text, because "League" appears in prose in the comments
    /// too and a string replace would quietly change a different file than it claims to.
    ///
    /// The schema file carries <c>//</c> comments, so it has to be parsed with them skipped -
    /// which drops them from what is re-serialised here. That costs nothing: this schema
    /// exists for the length of one assertion.
    /// </remarks>
    private static OffsetSchema WithoutLeague()
    {
        JsonNode root = JsonNode.Parse(
            File.ReadAllText(SchemaPath()),
            documentOptions: new System.Text.Json.JsonDocumentOptions
            {
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            })!;
        JsonObject fields = root["structs"]!["ServerDataOffsets"]!["fields"]!.AsObject();

        Assert.True(fields.Remove("League"), "the shipped schema no longer has the field this removes");

        return SchemaJson.Load(new MemoryStream(Encoding.UTF8.GetBytes(root.ToJsonString())));
    }
}
