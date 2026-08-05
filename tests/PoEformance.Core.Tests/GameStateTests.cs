using PoEformance.Core.Diagnostics;
using PoEformance.Core.Schema;
using PoEformance.Features;
using PoEformance.Game.Components;

namespace PoEformance.Core.Tests;

/// <summary>
/// Which of the game's states is in force, and that nothing acts outside the world.
/// </summary>
/// <remarks>
/// The game runs a STACK of states - loading, login, character select, the escape menu - and
/// only one of them is the world. Everything this tool does is about that one: drawing over a
/// loading screen paints markers onto a picture of nothing, and a flask key pressed into a
/// menu goes somewhere that is not the belt.
/// </remarks>
public class GameStateTests
{
    private const ulong Static = 0x0000_0400_0000_0000;
    private const ulong GameState = 0x0000_0400_0001_0000;
    private const ulong StateObjects = 0x0000_0400_0002_0000;
    private const ulong Stack = 0x0000_0400_0003_0000;

    /// <summary>The address the game gives state <paramref name="index"/>.</summary>
    private static ulong StateAt(int index) => StateObjects + ((ulong)index * 0x2000);

    /// <summary>
    /// Lays out the states array and a stack whose SECOND-last entry is the active state.
    /// </summary>
    private static (FakeMemoryReader Reader, OffsetSchema Schema) Game(int activeIndex, int stackDepth = 3)
    {
        OffsetSchema schema = RealSessionTests.Schema();
        StructDef gs = schema.Structs["GameState"];
        int entrySize = (int)gs.Constants["StateEntrySize"];
        int total = (int)gs.Constants["TotalStates"];

        var fake = new FakeMemoryReader().Place<ulong>(Static, GameState);

        for (int i = 0; i < total; i++)
        {
            fake.Place<ulong>(GameState + (ulong)(gs.OffsetOf("States") + (i * entrySize)), StateAt(i));
        }

        // The stack, and the entry that matters is the one BEFORE the end.
        for (int i = 0; i < stackDepth; i++)
        {
            fake.Place<ulong>(Stack + (ulong)(i * 8), StateAt(11));   // filler: some other state
        }

        ulong end = Stack + (ulong)(stackDepth * 8);
        fake.Place<ulong>(end - 0x10, StateAt(activeIndex));
        fake.Place<ulong>(GameState + (ulong)gs.OffsetOf("CurrentStateVecLast"), end);

        return (fake, schema);
    }

    [Theory]
    [InlineData(4, GameStateKind.InGame)]
    [InlineData(0, GameStateKind.AreaLoading)]
    [InlineData(3, GameStateKind.Escape)]
    [InlineData(6, GameStateKind.Login)]
    [InlineData(9, GameStateKind.SelectCharacter)]
    [InlineData(11, GameStateKind.Loading)]
    public void TheActiveStateIsNamedFromItsAddress(int index, GameStateKind expected)
    {
        // Identified by matching the active pointer against the states array rather than by
        // reading an index from somewhere: the array is already there, and a separate index
        // field would be one more thing that can drift on its own.
        (FakeMemoryReader fake, OffsetSchema schema) = Game(index);

        Assert.Equal(expected, GameChain.Resolve(fake, schema, Static).State);
    }

    [Fact]
    public void TheActiveStateIsTheSecondLastOnTheStack_NotTheLast()
    {
        // Not cosmetic, and not guessable: taking the final slot reports the wrong state for
        // the entire session. GameHelper2 reads it this way, and the fixture puts a DIFFERENT
        // state in the last slot so a version reading the wrong end fails here rather than
        // passing by luck.
        OffsetSchema schema = RealSessionTests.Schema();
        StructDef gs = schema.Structs["GameState"];
        int entrySize = (int)gs.Constants["StateEntrySize"];

        var fake = new FakeMemoryReader().Place<ulong>(Static, GameState);
        for (int i = 0; i < (int)gs.Constants["TotalStates"]; i++)
        {
            fake.Place<ulong>(GameState + (ulong)(gs.OffsetOf("States") + (i * entrySize)), StateAt(i));
        }

        ulong end = Stack + 0x40;
        fake.Place<ulong>(end - 0x10, StateAt(4));    // second-last: in game
        fake.Place<ulong>(end - 0x08, StateAt(0));    // last: area loading
        fake.Place<ulong>(GameState + (ulong)gs.OffsetOf("CurrentStateVecLast"), end);

        Assert.Equal(GameStateKind.InGame, GameChain.Resolve(fake, schema, Static).State);
    }

    [Fact]
    public void AStateTheListDoesNotKnowIsUnknown_NotInGame()
    {
        // The game may have states this list does not. Reported as unknown rather than
        // guessed at, which is what keeps it OUT of the in-game check.
        (FakeMemoryReader fake, OffsetSchema schema) = Game(4);
        StructDef gs = schema.Structs["GameState"];
        ulong end = Stack + 0x18;
        fake.Place<ulong>(end - 0x10, 0x0000_0400_0099_0000);   // in the array nowhere
        fake.Place<ulong>(GameState + (ulong)gs.OffsetOf("CurrentStateVecLast"), end);

        GameChainAddresses chain = GameChain.Resolve(fake, schema, Static);

        Assert.Equal(GameStateKind.Unknown, chain.State);
        Assert.False(chain.InGame);
    }

    [Fact]
    public void AnUnREADABLEStateFallsBackInsteadOfSwitchingEverythingOff()
    {
        // The distinction that keeps one drifted offset from silently disabling the whole
        // tool. "Unknown" means the read worked and named a state this list does not have -
        // a real answer, and a reason to stay out. "Unreadable" means the question could not
        // be asked at all, and refusing everything then would leave no symptom beyond the
        // tool going quiet, which is the worst way for a pointer to break.
        //
        // Found by the replay tests: a session recorded before anything read this field has
        // no bytes for it, so the whole scene vanished.
        OffsetSchema schema = RealSessionTests.Schema();
        var fake = new FakeMemoryReader().Place<ulong>(Static, GameState);

        GameChainAddresses chain = GameChain.Resolve(fake, schema, Static);

        Assert.Equal(GameStateKind.Unreadable, chain.State);
        Assert.True(new GameChainAddresses(1, 2, 3, 4, PlayerEntity: 5, State: GameStateKind.Unreadable).InGame);

        // ...while a state that READ fine and is simply not one of ours still stays out.
        Assert.False(new GameChainAddresses(1, 2, 3, 4, PlayerEntity: 5, State: GameStateKind.Unknown).InGame);
    }

    [Fact]
    public void BeingInTheWorldNeedsBOTHTheStateAndAPlayer()
    {
        // Neither half implies the other, and the player pointer is the one that misleads: it
        // SURVIVES a loading screen - it is the character being carried into the next area -
        // so on its own it reports "in game" over a picture of nothing.
        Assert.False(new GameChainAddresses(1, 2, 3, 4, PlayerEntity: 0, State: GameStateKind.InGame).InGame);
        Assert.False(new GameChainAddresses(1, 2, 3, 4, PlayerEntity: 5, State: GameStateKind.AreaLoading).InGame);
        Assert.True(new GameChainAddresses(1, 2, 3, 4, PlayerEntity: 5, State: GameStateKind.InGame).InGame);
    }

    [Fact]
    public void AutoFlaskDoesNothingOutsideTheWorld()
    {
        // The gate has to reach the FLASKS, not just the drawing: a key pressed during a
        // loading screen or in a menu still goes somewhere.
        var flask = new AutoFlask(
            [new FlaskRule("mana", VitalKind.Mana, 50, Key: 0x31)],
            enabled: true);

        // Mana at a tenth, which is well past the threshold: the only reason nothing happens
        // is the one being tested.
        var healthy = new Vitals(
            new Vital(100, 100, 0, 0), new Vital(10, 100, 0, 0), new Vital(0, 0, 0, 0));

        FlaskTick playing = flask.Evaluate(healthy, gameFocused: true, nowMs: 1000, inGame: true);
        Assert.NotEmpty(playing.Used);

        FlaskTick loading = flask.Evaluate(healthy, gameFocused: true, nowMs: 5000, inGame: false);
        Assert.Empty(loading.Used);
        Assert.Equal("not in game", loading.Reason);
    }

    [Fact]
    public void NotBeingInTheWorldIsCheckedBeforeFocus()
    {
        // Order, so the readout answers the question actually asked. "Game not focused"
        // during a loading screen sends someone looking for a focus problem that is not
        // there - the reason string is the only thing a silent no-op can say.
        var flask = new AutoFlask([new FlaskRule("mana", VitalKind.Mana, 50, Key: 0x31)], enabled: true);
        var healthy = new Vitals(
            new Vital(100, 100, 0, 0), new Vital(10, 100, 0, 0), new Vital(0, 0, 0, 0));

        Assert.Equal("not in game", flask.Evaluate(healthy, gameFocused: false, 1000, inGame: false).Reason);
    }
}
