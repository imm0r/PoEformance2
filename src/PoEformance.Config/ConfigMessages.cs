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
    [property: JsonPropertyName("minLootRarity")] string MinLootRarity,
    [property: JsonPropertyName("showTerrain")] bool ShowTerrain,
    [property: JsonPropertyName("terrainColour")] string TerrainColour,
    [property: JsonPropertyName("terrainThickness")] int TerrainThickness,
    [property: JsonPropertyName("terrain")] string Terrain);

/// <summary>One marker on the page's map, in outline-pixel coordinates.</summary>
public sealed record MapMarker(
    [property: JsonPropertyName("x")] float X,
    [property: JsonPropertyName("y")] float Y,
    [property: JsonPropertyName("kind")] string Kind);

/// <summary>
/// The map panel: where things are, but NOT the layout itself.
/// </summary>
/// <remarks>
/// The layout is thousands of numbers and changes only on an area change, so it travels
/// separately and on request. This block rides every state push, which is what keeps the
/// per-second message small enough to send at that rate.
/// </remarks>
/// <param name="Area">
/// The area's instance hash. The page compares it against the layout it is holding and
/// asks for a new one when they differ - so a portal refreshes the map without the host
/// having to track what the page knows.
/// </param>
public sealed record MapStateView(
    [property: JsonPropertyName("area")] uint Area,
    [property: JsonPropertyName("hasLayout")] bool HasLayout,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("playerX")] float PlayerX,
    [property: JsonPropertyName("playerY")] float PlayerY,
    [property: JsonPropertyName("markers")] IReadOnlyList<MapMarker> Markers);

/// <summary>The layout itself, sent once per area in reply to a request.</summary>
public sealed record MapLayoutMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("area")] uint Area,
    [property: JsonPropertyName("layout")] MapLayout Layout);

/// <summary>One fact the rule editor may offer, as the page needs to render it.</summary>
/// <param name="Shape">"Flag" or "Number" - whether the editor shows a comparison at all.</param>
/// <param name="Argument">"None", "Text", "Slot", "Distance" or "Seconds".</param>
public sealed record FactView(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("shape")] string Shape,
    [property: JsonPropertyName("argument")] string Argument,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("help")] string Help);

/// <summary>
/// What the rule editor is allowed to offer, sent once.
/// </summary>
/// <remarks>
/// GENERATED from <see cref="RuleFacts.All"/> and <see cref="RuleKeys.Names"/> rather than
/// written into the page, which is the same argument the style editor is built on: a
/// hand-written list of conditions is how a tool ends up with thirty-four facts the engine can
/// evaluate and twenty-nine the editor can offer. Sent once - it cannot change while the tool
/// runs - and asked for by the page rather than pushed with every state, because it is static
/// and the state travels once a second.
/// </remarks>
public sealed record RuleCatalogue(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("facts")] IReadOnlyList<FactView> Facts,
    [property: JsonPropertyName("keys")] IReadOnlyList<string> Keys,
    [property: JsonPropertyName("effects")] IReadOnlyList<string> Effects,
    [property: JsonPropertyName("comparisons")] IReadOnlyList<string> Comparisons)
{
    /// <summary>Builds the catalogue from the engine's own tables.</summary>
    public static RuleCatalogue Build()
    {
        var facts = new List<FactView>(RuleFacts.All.Count);
        foreach (FactInfo info in RuleFacts.All)
        {
            facts.Add(new FactView(
                info.Name,
                info.Shape.ToString(),
                info.Argument.ToString(),
                info.Unit,
                info.Help));
        }

        return new RuleCatalogue(
            "ruleCatalogue",
            facts,
            RuleKeys.Names,
            Enum.GetNames<RuleEffectKind>(),
            Enum.GetNames<Compare>());
    }
}

/// <summary>The rules panel: the settings themselves, plus what the engine is doing with them.</summary>
/// <param name="Status">
/// The engine's last reason. Worth a line of its own for the reason auto-flask has one: a rule
/// that is armed and silent and a rule that cannot fire look identical from the page.
/// </param>
/// <param name="KeySource">
/// Where the flask bindings came from, for the effects bound to a belt slot rather than to a
/// named key.
/// </param>
/// <param name="Buffs">
/// What the character has had on, so a buff condition can be picked instead of guessed at.
/// </param>
public sealed record RulesView(
    [property: JsonPropertyName("settings")] RuleSettings Settings,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("acted")] int Acted,
    [property: JsonPropertyName("keySource")] string KeySource,
    [property: JsonPropertyName("shapes")] IReadOnlyDictionary<string, RuleShape> Shapes,
    [property: JsonPropertyName("buffs")] IReadOnlyList<SeenBuff> Buffs,
    [property: JsonPropertyName("buffRead")] string BuffRead,
    [property: JsonPropertyName("reader")] string Reader)
{
    /// <summary>Builds the panel, including a text and a graph for every rule.</summary>
    public static RulesView Of(RuleEngine engine, string keySource, BuffWatch buffs, string reader = "")
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(buffs);

        var shapes = new Dictionary<string, RuleShape>(StringComparer.Ordinal);
        RuleSettings settings = engine.Settings;
        foreach (RuleProfile profile in settings.Profiles)
        {
            foreach (RuleGroup group in profile.Groups)
            {
                foreach (Rule rule in group.Rules)
                {
                    shapes[rule.Id] = new RuleShape(
                        RuleExpression.Write(rule.Condition),

                        // The stored layout when there is one, and a drawn one otherwise, so
                        // opening the canvas on a rule somebody typed shows the same rule
                        // rather than an empty sheet.
                        rule.Graph ?? RuleGraph.From(rule.Condition));
                }
            }
        }

        return new RulesView(
            settings, engine.LastTick.Reason, engine.Acted, keySource, shapes, buffs.Seen,
            buffs.LastRead, reader);
    }
}

/// <summary>The two other ways of looking at one rule's condition.</summary>
/// <remarks>
/// Beside the settings rather than on the rule, and that is the whole point: the settings
/// record is exactly what the file holds, and the file is meant to be hand-edited. A "text"
/// field on the stored rule would be a field that looks editable, is written on every save and
/// is ignored on every load - which is a worse trap than not offering it.
/// </remarks>
public sealed record RuleShape(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("graph")] RuleGraph Graph);

/// <summary>The host's answer to a condition somebody typed.</summary>
/// <param name="Column">Where the trouble is, counted from 1, so the page can point at it.</param>
public sealed record ConditionMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("column")] int Column,
    [property: JsonPropertyName("condition")] RuleCondition? Condition);

/// <summary>
/// The update panel: what is running, what is published, and how far an install has got.
/// </summary>
/// <remarks>
/// ONE BLOCK FOR THE WHOLE FEATURE, riding the once-a-second state push like everything else
/// here. It carries the changelog, which is the largest thing on this bridge that is not the
/// map layout - a few kilobytes of markdown at most, and only while a release is known. The
/// alternative, fetching it on request the way the map layout is fetched, would buy back those
/// kilobytes at the cost of a second protocol for a panel nobody has open for long.
/// </remarks>
/// <param name="Verdict">
/// The check's decision by name - "NotChecked", "UpToDate", "Available", "CannotCompare" or
/// "Failed". The page shows the status line either way; the verdict is what decides whether
/// there is a button.
/// </param>
/// <param name="Current">This build in words, or why it cannot say.</param>
/// <param name="Available">The published build in words. Empty until one has been read.</param>
/// <param name="Notes">The release body - the changelog, as the release wrote it.</param>
/// <param name="Step">
/// How far an install has got - "Idle", "Downloading", "Extracting", "Ready" or "Failed".
/// </param>
/// <param name="Outcome">
/// What the last restart was: "updated", "failed", or empty. This is the notification the
/// user gets for an update that has already happened, which is the only moment the tool can
/// report on one - the process that did the work no longer exists.
/// </param>
public sealed record UpdateView(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("verdict")] string Verdict,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("busy")] bool Busy,
    [property: JsonPropertyName("offering")] bool Offering,
    [property: JsonPropertyName("current")] string Current,
    [property: JsonPropertyName("available")] string Available,
    [property: JsonPropertyName("releaseName")] string ReleaseName,
    [property: JsonPropertyName("releaseTag")] string ReleaseTag,
    [property: JsonPropertyName("notes")] string Notes,
    [property: JsonPropertyName("releaseSize")] long ReleaseSize,
    [property: JsonPropertyName("checked")] string Checked,
    [property: JsonPropertyName("step")] string Step,
    [property: JsonPropertyName("installStatus")] string InstallStatus,
    [property: JsonPropertyName("received")] long Received,
    [property: JsonPropertyName("total")] long Total,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("outcomeVersion")] string OutcomeVersion,
    [property: JsonPropertyName("log")] string Log);

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
    [property: JsonPropertyName("overlay")] OverlayView? Overlay = null,
    [property: JsonPropertyName("map")] MapStateView? Map = null,
    [property: JsonPropertyName("rules")] RulesView? Rules = null,
    [property: JsonPropertyName("evasion")] EvasionView? Evasion = null,
    [property: JsonPropertyName("ground")] GroundView? Ground = null,
    [property: JsonPropertyName("update")] UpdateView? Update = null);

/// <summary>The evasion panel: the settings, the key, and why nothing is happening.</summary>
/// <param name="Status">
/// The planner's last reason. Worth its own line here as well as on the overlay, because this
/// feature has more ways of being armed and silent than any other in the tool - both gates, a
/// rarity floor nothing in the area reaches, an unset key, the cooldown - and they look
/// identical from the outside.
/// </param>
/// <param name="KeyName">
/// The dodge key in words, or "unbound". Editable here, unlike the flask keys: this is the one
/// key the tool does NOT read from the game, because nobody has established what the game calls
/// it - see <see cref="Features.DodgeKeyHints"/>.
/// </param>
/// <param name="KeyHints">
/// Lines from the game's own config that MIGHT be the dodge binding, so somebody can fill the
/// setting in without opening the ini. Suggestions, never a reading.
/// </param>
/// <param name="MoveHints">
/// The same for the movement keys, which the steering holds. Shown for the same reason and with
/// the same caveat: the W A S D defaults are what the game ships with, not something read.
/// </param>
/// <param name="MoveKeyNames">The four movement keys in words, so a rebound one is visible.</param>
public sealed record EvasionView(
    [property: JsonPropertyName("settings")] EvasionSettings Settings,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("keyName")] string KeyName,
    [property: JsonPropertyName("keyHints")] IReadOnlyList<string> KeyHints,
    [property: JsonPropertyName("moveHints")] IReadOnlyList<string> MoveHints,
    [property: JsonPropertyName("moveKeyNames")] string MoveKeyNames);

/// <summary>The dangerous-ground panel: the rules, and what the session has actually seen.</summary>
/// <param name="Rules">
/// The path rules as they stand. Only this slice of the tracker's settings crosses to the page -
/// the overlay's own tab owns the rest - and the host merges it back rather than replacing the
/// whole record, so a switch that lives only on the overlay cannot be reset by a save from here.
/// </param>
/// <param name="Seen">
/// Every kind of dangerous-looking ground the session has met, so a path is PICKED rather than
/// typed. The list is the whole reason this panel exists in the config window: a metadata path
/// is written nowhere a player can see it, and the ground that killed them is gone by the time
/// they have alt-tabbed here.
/// </param>
/// <param name="Reading">Why the list looks the way it does - see GroundWatch.LastRead.</param>
public sealed record GroundView(
    [property: JsonPropertyName("rules")] IReadOnlyList<GroundDangerRule> Rules,
    [property: JsonPropertyName("seen")] IReadOnlyList<SeenGround> Seen,
    [property: JsonPropertyName("reading")] string Reading);

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
[JsonSerializable(typeof(MapLayoutMessage))]
[JsonSerializable(typeof(RuleCatalogue))]
[JsonSerializable(typeof(RuleSettings))]
[JsonSerializable(typeof(ConditionMessage))]
[JsonSerializable(typeof(EvasionSettings))]
[JsonSerializable(typeof(UpdateSettings))]
[JsonSerializable(typeof(List<GroundDangerRule>))]
public sealed partial class ConfigJsonContext : JsonSerializerContext;
