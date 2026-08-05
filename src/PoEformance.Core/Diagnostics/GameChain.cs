using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Core.Diagnostics;

/// <summary>
/// Which of the game's own states is in force.
/// </summary>
/// <remarks>
/// The game runs a STACK of states - a loading screen, the login screen, character select,
/// the escape menu - and only one of them is the world. Everything this tool does is about
/// that one: drawing over a loading screen puts markers on a picture of nothing, and pressing
/// a flask key into a menu is worse than useless.
///
/// The order is the game's own, so the index resolves straight into it. Taken from
/// GameHelper2's GameStateTypes, which lists thirteen.
/// </remarks>
public enum GameStateKind
{
    AreaLoading = 0,
    ChangePassword,
    Credits,
    Escape,
    InGame,
    PreGame,
    Login,
    Waiting,
    CreateCharacter,
    SelectCharacter,
    DeleteCharacter,
    Loading,
    Unknown,

    /// <summary>The chain did not resolve at all - the game is not running or not ready.</summary>
    NotLoaded = 100,

    /// <summary>
    /// The state field itself could not be read.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT the same as <see cref="Unknown"/>. Unknown means the read worked and
    /// named a state this list does not have, which is a real answer and a reason to stay
    /// out. This means the question could not be asked at all - a drifted offset, or a
    /// recording made before anything read this field - and treating that as "not in the
    /// world" would let one unreadable pointer switch off every feature at once, with no
    /// symptom beyond the whole tool going quiet.
    ///
    /// So it falls back to the older signal: a resolved player entity. Weaker, and it is
    /// reported as such rather than hidden.
    /// </remarks>
    Unreadable = 101,
}

/// <summary>The key addresses along the static -> player pointer chain.</summary>
/// <remarks>
/// Any of these can be 0 when the game is not in a playable area (in a menu, on a loading
/// screen). Callers check the one they need rather than assuming the whole chain resolved.
/// </remarks>
public readonly record struct GameChainAddresses(
    ulong GameState,
    ulong InGameState,
    ulong AreaInstance,
    ulong WorldData,
    ulong PlayerEntity,
    ulong UiRoot = 0,
    GameStateKind State = GameStateKind.NotLoaded)
{
    /// <summary>
    /// True when the game is in the world AND the chain reached a player entity.
    /// </summary>
    /// <remarks>
    /// Both halves are needed and neither implies the other. The player pointer survives a
    /// loading screen - it is the character being carried into the next area - so it alone
    /// reports "in game" over a picture of nothing; and the state alone says nothing about
    /// whether the entity behind it has been rebuilt yet.
    /// </remarks>
    public bool InGame => PlayerEntity != 0
                          && State is GameStateKind.InGame or GameStateKind.Unreadable;
}

/// <summary>
/// Resolves the pointer chain from the GameStates static to the local player entity, using
/// the schema for every offset.
/// </summary>
/// <remarks>
/// One place owns the walk so the drift report, the player probe and every future consumer
/// agree on it - including the two lessons that were only visible in real memory:
/// InGameState is picked out of the state array by a well-known index, and
/// LocalPlayerStruct is INLINE in AreaInstance (its base is the ADDRESS of the PlayerInfo
/// field, not the value stored there).
/// </remarks>
public static class GameChain
{
    public static GameChainAddresses Resolve(IMemoryReader reader, OffsetSchema schema, ulong gameStatesStatic)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);

        ulong gameState = reader.ReadPointer(gameStatesStatic);
        if (gameState == 0)
        {
            return default;
        }

        StructDef gs = schema.Structs["GameState"];
        ulong statesBase = gameState + (ulong)gs.OffsetOf("States");
        int entrySize = (int)gs.Constants["StateEntrySize"];
        ulong inGameState = reader.ReadPointer(
            statesBase + (ulong)(gs.Constants["InGameStateIndex"] * entrySize));

        GameStateKind state = ResolveState(reader, schema, gameState, statesBase, entrySize);

        if (inGameState == 0)
        {
            return new GameChainAddresses(gameState, 0, 0, 0, 0, 0, state);
        }

        StructDef igs = schema.Structs["InGameState"];
        ulong areaInstance = reader.ReadPointer(inGameState + (ulong)igs.OffsetOf("AreaInstanceData"));
        ulong worldData = reader.ReadPointer(inGameState + (ulong)igs.OffsetOf("WorldData"));

        // The UI manager, which owns the map elements. Resolved here so the one walk serves
        // both halves of the tool - the world overlay and anything reading the interface.
        ulong uiRoot = reader.ReadPointer(inGameState + (ulong)igs.OffsetOf("UiRootStructPtr"));

        if (areaInstance == 0)
        {
            return new GameChainAddresses(gameState, inGameState, 0, worldData, 0, uiRoot, state);
        }

        // LocalPlayerStruct is inline: its base is the address of AreaInstance+PlayerInfo,
        // and LocalPlayerPtr within it points at the player entity.
        StructDef ai = schema.Structs["AreaInstance"];
        StructDef lp = schema.Structs["LocalPlayerStruct"];
        ulong playerBase = areaInstance + (ulong)ai.OffsetOf("PlayerInfo");
        ulong playerEntity = reader.ReadPointer(playerBase + (ulong)lp.OffsetOf("LocalPlayerPtr"));

        return new GameChainAddresses(
            gameState, inGameState, areaInstance, worldData, playerEntity, uiRoot, state);
    }

    /// <summary>
    /// Reads which state the game is actually in.
    /// </summary>
    /// <remarks>
    /// The state in force is the SECOND-last entry of the state stack, not the last - that is
    /// how GameHelper2 reads it, and the difference is not cosmetic: the final slot is not the
    /// active state, so taking it reports the wrong state for the whole session.
    ///
    /// Identified by ADDRESS rather than by an index stored somewhere: the states array holds
    /// each state object's pointer at a known index, so matching the active pointer against it
    /// names the state without needing another field that could drift on its own.
    /// </remarks>
    private static GameStateKind ResolveState(
        IMemoryReader reader, OffsetSchema schema, ulong gameState, ulong statesBase, int entrySize)
    {
        StructDef gs = schema.Structs["GameState"];
        ulong stackEnd = reader.ReadPointer(gameState + (ulong)gs.OffsetOf("CurrentStateVecLast"));
        if (!MemoryReaderExtensions.IsPlausiblePointer(stackEnd) || stackEnd < 0x10)
        {
            return GameStateKind.Unreadable;
        }

        ulong active = reader.ReadPointer(stackEnd - 0x10);
        if (active == 0)
        {
            return GameStateKind.Unreadable;
        }

        int total = (int)gs.Constants["TotalStates"];
        for (int i = 0; i < total; i++)
        {
            if (reader.ReadPointer(statesBase + (ulong)(i * entrySize)) == active)
            {
                return (GameStateKind)i;
            }
        }

        // A state the game has that this list does not. Reported as unknown rather than
        // guessed at, which keeps it OUT of the in-game check.
        return GameStateKind.Unknown;
    }
}
