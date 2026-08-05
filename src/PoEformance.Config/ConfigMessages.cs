using System.Text.Json;
using System.Text.Json.Serialization;
using PoEformance.Features;

namespace PoEformance.Config;

/// <summary>A message from the page to the host: a command name plus optional payload.</summary>
public sealed record ConfigRequest(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("payload")] JsonElement Payload = default);

/// <summary>One belt slot as the page shows it: the setting, plus what is actually in it.</summary>
/// <param name="Key">
/// The key that uses this flask, READ FROM THE GAME and shown read-only. It is the game's
/// setting, not ours - see <see cref="FlaskKeyBindings"/> for why it is not editable here.
/// </param>
/// <param name="Item">The equipped flask's name, or empty when the slot is empty.</param>
/// <param name="Charges">"12/9" - held over per-use cost. Empty when nothing is equipped.</param>
/// <param name="IsCharm">
/// Charms sit in the same belt but the game triggers them itself, so a rule on that slot
/// could never do anything. The page says so rather than letting one be armed silently.
/// </param>
public sealed record FlaskSlotView(
    [property: JsonPropertyName("slot")] int Slot,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("vital")] string Vital,
    [property: JsonPropertyName("thresholdPercent")] int ThresholdPercent,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("item")] string Item,
    [property: JsonPropertyName("charges")] string Charges,
    [property: JsonPropertyName("isCharm")] bool IsCharm);

/// <summary>The auto-flask panel: the master switch, the slots, and why nothing fired.</summary>
/// <param name="KeySource">
/// Where the keys came from, in words. Worth showing: bindings read from the game's config
/// and bindings assumed from the default layout behave identically right up until someone
/// has rebound a flask, and then the only symptom is a tool that appears to do nothing.
/// </param>
/// <param name="Status">The engine's last reason, so "nothing happened" explains itself.</param>
public sealed record AutoFlaskView(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("keySource")] string KeySource,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("slots")] IReadOnlyList<FlaskSlotView> Slots);

/// <summary>The overlay panel: what is drawn over the game.</summary>
/// <param name="MinLootRarity">
/// The worst drop still marked, by name. Currency is never filtered - it has no rarity to
/// compare against and is the last thing anyone wants hidden.
/// </param>
public sealed record OverlayView(
    [property: JsonPropertyName("minLootRarity")] string MinLootRarity);

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
    [property: JsonPropertyName("entityCount")] int EntityCount,
    [property: JsonPropertyName("autoFlask")] AutoFlaskView? AutoFlask = null,
    [property: JsonPropertyName("overlay")] OverlayView? Overlay = null);

/// <summary>
/// Source-generated JSON for the bridge.
/// </summary>
/// <remarks>
/// Source-generated rather than reflection-based because the whole point of this window is
/// proving the config stack survives Native AOT - reflection serialisation is exactly the
/// kind of dependency that dies there, silently, at runtime.
///
/// String enums so the wire carries "Mana" rather than 1: the page shows the value directly
/// and the settings file is meant to be hand-editable, and neither survives an ordinal that
/// shifts the next time the enum gains a member.
/// </remarks>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ConfigRequest))]
[JsonSerializable(typeof(ConfigState))]
[JsonSerializable(typeof(AutoFlaskSettings))]
[JsonSerializable(typeof(OverlaySettings))]
public sealed partial class ConfigJsonContext : JsonSerializerContext;
