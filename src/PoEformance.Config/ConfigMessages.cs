using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoEformance.Config;

/// <summary>A message from the page to the host: a command name plus optional payload.</summary>
public sealed record ConfigRequest(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("payload")] JsonElement Payload = default);

/// <summary>The host's state push: everything the config page shows.</summary>
/// <remarks>
/// One flat record on purpose. The page re-renders from whole states rather than patching
/// fields, so adding a value here is one property plus one line of JS - no protocol dance.
/// </remarks>
public sealed record ConfigState(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("toolVersion")] string ToolVersion,
    [property: JsonPropertyName("gameVersion")] string GameVersion,
    [property: JsonPropertyName("attached")] bool Attached,
    [property: JsonPropertyName("processId")] int ProcessId,
    [property: JsonPropertyName("staticsFound")] int StaticsFound,
    [property: JsonPropertyName("staticsTotal")] int StaticsTotal,
    [property: JsonPropertyName("inGame")] bool InGame,
    [property: JsonPropertyName("entityCount")] int EntityCount);

/// <summary>
/// Source-generated JSON for the bridge.
/// </summary>
/// <remarks>
/// Source-generated rather than reflection-based because the whole point of this window is
/// proving the config stack survives Native AOT - reflection serialisation is exactly the
/// kind of dependency that dies there, silently, at runtime.
/// </remarks>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ConfigRequest))]
[JsonSerializable(typeof(ConfigState))]
public sealed partial class ConfigJsonContext : JsonSerializerContext;
