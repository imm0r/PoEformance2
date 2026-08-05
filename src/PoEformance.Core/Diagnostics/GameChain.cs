using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Core.Diagnostics;

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
    ulong PlayerEntity)
{
    /// <summary>True when the chain reached a player entity - i.e. we are in an area.</summary>
    public bool InGame => PlayerEntity != 0;
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
        ulong inGameState = reader.ReadPointer(
            statesBase + (ulong)(gs.Constants["InGameStateIndex"] * gs.Constants["StateEntrySize"]));
        if (inGameState == 0)
        {
            return new GameChainAddresses(gameState, 0, 0, 0, 0);
        }

        StructDef igs = schema.Structs["InGameState"];
        ulong areaInstance = reader.ReadPointer(inGameState + (ulong)igs.OffsetOf("AreaInstanceData"));
        ulong worldData = reader.ReadPointer(inGameState + (ulong)igs.OffsetOf("WorldData"));
        if (areaInstance == 0)
        {
            return new GameChainAddresses(gameState, inGameState, 0, worldData, 0);
        }

        // LocalPlayerStruct is inline: its base is the address of AreaInstance+PlayerInfo,
        // and LocalPlayerPtr within it points at the player entity.
        StructDef ai = schema.Structs["AreaInstance"];
        StructDef lp = schema.Structs["LocalPlayerStruct"];
        ulong playerBase = areaInstance + (ulong)ai.OffsetOf("PlayerInfo");
        ulong playerEntity = reader.ReadPointer(playerBase + (ulong)lp.OffsetOf("LocalPlayerPtr"));

        return new GameChainAddresses(gameState, inGameState, areaInstance, worldData, playerEntity);
    }
}
