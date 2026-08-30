using System.Text.Json;
using System.Text.Json.Serialization;
using PoEformance.Game.Components;

namespace PoEformance.Features;

/// <summary>
/// Which monsters a gate lets through, and what to do about them.
/// </summary>
/// <remarks>
/// TWO GATES RATHER THAN ONE, and they are deliberately separate settings with separate
/// defaults: drawing a marker is free and pressing a key is not. Somebody who wants to SEE
/// everything coming and only have the tool ACT on a rare's slam is the ordinary case, and one
/// shared threshold cannot express it.
/// </remarks>
/// <param name="Enabled">Whether this gate does anything at all.</param>
/// <param name="FromRarity">
/// The least rare monster this gate lets through. <see cref="ItemRarity.Normal"/> is every
/// monster; <see cref="ItemRarity.Unique"/> is bosses only.
/// </param>
/// <param name="OnlyPaths">
/// When non-empty, only monsters whose metadata path contains one of these. The path is the
/// game's own identifier - Metadata/Monsters/Goatman/GoatmanLeaper - so a substring is a
/// monster TYPE without needing a table of names.
/// </param>
/// <param name="IgnorePaths">Monsters whose path contains one of these never pass, whatever else says.</param>
public sealed record EvasionGate(
    [property: JsonPropertyName("enabled")] bool Enabled = false,
    [property: JsonPropertyName("fromRarity")] ItemRarity FromRarity = ItemRarity.Normal,
    [property: JsonPropertyName("onlyPaths")] IReadOnlyList<string>? OnlyPaths = null,
    [property: JsonPropertyName("ignorePaths")] IReadOnlyList<string>? IgnorePaths = null)
{
    /// <summary>Whether a monster of this rarity and path gets through.</summary>
    /// <remarks>
    /// An UNKNOWN rarity passes when the floor is Normal and is refused otherwise, which is the
    /// safe way round for each: a monster whose rarity would not read is still worth a marker,
    /// and is not worth pressing a key over.
    /// </remarks>
    public bool Admits(ItemRarity rarity, string path)
    {
        if (!Enabled)
        {
            return false;
        }

        if (rarity < FromRarity && !(rarity == ItemRarity.Unknown && FromRarity == ItemRarity.Normal))
        {
            return false;
        }

        path ??= string.Empty;

        foreach (string ignore in IgnorePaths ?? [])
        {
            if (ignore.Length > 0 && path.Contains(ignore, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        IReadOnlyList<string> only = OnlyPaths ?? [];
        if (only.Count == 0)
        {
            return true;
        }

        foreach (string wanted in only)
        {
            if (wanted.Length > 0 && path.Contains(wanted, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Drops empty filter strings, which would otherwise match everything.</summary>
    public EvasionGate Normalised() => this with
    {
        FromRarity = FromRarity < ItemRarity.Normal ? ItemRarity.Normal : FromRarity,
        OnlyPaths = Clean(OnlyPaths),
        IgnorePaths = Clean(IgnorePaths),
    };

    private static IReadOnlyList<string>? Clean(IReadOnlyList<string>? paths)
        => paths is null ? null : [.. paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim())];
}

/// <summary>
/// Everything the user decides about seeing and avoiding incoming attacks.
/// </summary>
/// <remarks>
/// ITS OWN FILE, beside autoflask.json rather than inside the tracker's, because this is the
/// second feature in the tool that can PRESS A KEY. The tracker's settings decide what is drawn;
/// these decide what is drawn AND what is done, and the two halves are worth being able to find
/// in one place when the question is "why did my character just roll".
///
/// EVERYTHING IS OFF BY DEFAULT, including the drawing. A tool that starts acting on its own the
/// first time it is run is not one worth shipping, and the same argument applies a second time
/// over to the half that synthesises input.
/// </remarks>
/// <param name="Warn">Draw a marker where an incoming action will land.</param>
/// <param name="Act">Press the dodge key when one is about to land on the player.</param>
/// <param name="DangerRadius">
/// How close an action's landing spot must be to the player to count as aimed at them, in world
/// units.
/// </param>
/// <param name="CooldownMs">
/// Shortest gap between two dodge presses. Not cosmetic: the planner re-decides on every read,
/// and a slam stays committed for as long as its wind-up lasts, so without this one attack
/// would spend every roll charge the character has.
/// </param>
/// <param name="DodgeKey">
/// Virtual-key code to press. 0 means unset, and with it the tool warns but never acts.
///
/// THIS IS THE ONE KEY THE TOOL DOES NOT READ FROM THE GAME, and the difference from the flask
/// keys is deliberate: their config spelling was established against a real file, and nobody has
/// established what this game calls the dodge roll. Reading a plausible-looking line instead
/// would be a guess dressed as a measurement, and it would send a key the player never bound.
/// The AHK tool settles it the same way - its action hotkeys are always keys the person chose.
/// <see cref="DodgeKeyHints"/> shows the candidates in the config to save somebody opening it.
/// </param>
/// <param name="Steer">
/// Hold movement keys during the roll so it goes where the tool chooses, rather than where the
/// player was already pointing.
///
/// ITS OWN SWITCH, DEFAULT OFF, on top of <paramref name="Act"/> being on. Pressing the dodge key
/// leaves the direction entirely with the player and has been used for hours; this takes the
/// steering away for the length of a roll, which is a strictly larger thing to hand a tool. The
/// two are worth being able to switch on separately.
///
/// IT WORKS, over two complete maps (2026-08-29, the owner playing): the roll goes the way the
/// planner chose, WSAD can be held down throughout, and movement continues in the held direction
/// the moment the roll ends - no key to press again, no stutter. That last part is the whole
/// point of <c>PhysicalKeys</c> and it is the half that could only ever be confirmed by playing,
/// since a recording cannot show a finger still on a key.
///
/// THE DEFAULT STAYS OFF ANYWAY. Not because anything is unproven now, but because a tool that
/// takes the movement keys over should be something a person switches on deliberately.
/// </param>
/// <param name="RollDistance">
/// How far a roll travels, in world units - what the steering scores directions over.
///
/// MEASURED, NOT CHOSEN: the five rolls in <c>session-2026-08-monsters.rec</c> covered 141, 232,
/// 391, 509 and 520 world units (see <c>DodgeRollDirectionTests</c>), so the ordinary roll is
/// around four hundred and the short ones are rolls that met something. It is a setting because a
/// character's movement speed changes it and nobody has measured that relationship.
/// </param>
/// <param name="SteerHoldMs">
/// The LONGEST the steering keys may stay down around the roll. A ceiling, not a duration.
///
/// IT USED TO BE THE DURATION and that is the interesting part of its history. The keys have to
/// stay down across one of the game's frames - input that goes down and up between two of the
/// game's polls can be missed entirely - and nothing told the tool when a frame had passed, so
/// this number was how long to hold and it was a JUDGEMENT. It got two readings against it:
/// 60 ms and 20 ms both work, on the owner's machine (2026-08-29). One frame is 16.7 ms at
/// 60 fps, 33 ms at 30, 62 ms at 16, so 20 clears a frame only above roughly 50 fps while 60
/// covers everything down to about 16 - which is exactly the shape of those two readings, since
/// the owner plays well above 50. The failure below the line is SILENT: the roll simply goes
/// where the player was already pointing, which looks like the steering choosing that direction.
///
/// IT IS A CEILING NOW: <c>RollWatch</c> asks the game whether the roll has started and
/// <c>DodgeSteer</c> gives the keys back the moment it has. The claim that used to stand here was
/// that this costs "one frame and a little, however long that frame took".
///
/// THAT CLAIM IS REFUTED, by the owner playing with the ceiling raised to 200 so the measurement
/// could not be truncated (2026-08-29): the confirmation arrives in 49-62 ms, tightly, with
/// nothing on the ceiling. At 60 fps that is THREE FRAMES, not one. And the same machine had
/// already shown 20 ms working as a flat hold - so the game reads the keys long before the
/// animation id turns over, and this signal is a LATE, downstream consequence of the roll rather
/// than the moment the input was used. The premise was sound and the signal is not: the roll
/// starting is indeed the input having been used, but the roll starting is not what the animation
/// id reports when it changes.
///
/// SO THE CONFIRMATION BUYS NOTHING HERE. It lands at 55-62 ms, which is where the guessed 60 ms
/// already was. On the owner's machine the shortest exposure available is the one the flat hold
/// gave: 20 ms, proven by playing. What the ceiling is still for is the case where nothing
/// confirms - no animation table, no Actor address, a roll chained out of another one.
///
/// THE DEFAULT STAYS AT 60 anyway, and now for a smaller reason than before: it is the value
/// sized for the slowest machine nobody has measured, and no machine has been measured except
/// this one. On THIS one, 20 is better - it is the shortest hold that was shown to work, and the
/// confirmation simply never fires inside it. That is a setting, not a default.
///
/// SHORTER IS STILL SAFER WHERE IT WORKS, and worth saying: the hold is the window in which the
/// tool owns the movement keys, so a player who lets go of one inside it gets it pressed back
/// down by the restore (see <c>PhysicalKeys</c> for why that window cannot be closed entirely).
/// The confirmation is what closes most of it, on every machine, without anyone choosing a
/// number: <see cref="RollTimes"/> reports what the last few rolls actually cost, on the overlay
/// and in the config window beside this setting. A spread rather than the latest reading,
/// because the measurement is taken during a fight - where one number is overwritten before it
/// can be read - and because one confirmation cannot tell a slow machine from one stutter.
/// </param>
/// <param name="Keys">The movement keys to steer with. See <see cref="MovementKeys"/>.</param>
public sealed record EvasionSettings(
    [property: JsonPropertyName("warn")] EvasionGate? Warn = null,
    [property: JsonPropertyName("act")] EvasionGate? Act = null,
    [property: JsonPropertyName("dangerRadius")] float DangerRadius = 90f,
    [property: JsonPropertyName("cooldownMs")] int CooldownMs = 1200,
    [property: JsonPropertyName("dodgeKey")] int DodgeKey = 0,
    [property: JsonPropertyName("steer")] bool Steer = false,
    [property: JsonPropertyName("rollDistance")] float RollDistance = 400f,
    [property: JsonPropertyName("steerHoldMs")] int SteerHoldMs = 60,
    [property: JsonPropertyName("keys")] MovementKeys? Keys = null,
    [property: JsonPropertyName("onlyDangerousAnimations")] bool OnlyDangerousAnimations = true,
    [property: JsonPropertyName("markerColour")] string MarkerColour = "#C8FF3C28",
    [property: JsonPropertyName("aimedColour")] string AimedColour = "#FFFF0000",
    [property: JsonPropertyName("markerRadius")] float MarkerRadius = 14f,
    [property: JsonPropertyName("thickness")] float Thickness = 2f,
    [property: JsonPropertyName("showLine")] bool ShowLine = true,
    [property: JsonPropertyName("showName")] bool ShowName = false)
{
    /// <summary>Off, and drawing for everything / acting on rares once switched on.</summary>
    /// <remarks>
    /// The two floors differ on purpose. A marker for an ordinary monster costs a ring on the
    /// screen; a keystroke for one costs a roll charge and a moment of not controlling your own
    /// character, and white monsters are most of what an area contains.
    /// </remarks>
    public static EvasionSettings Default { get; } = new(
        Warn: new EvasionGate(Enabled: false, FromRarity: ItemRarity.Normal),
        Act: new EvasionGate(Enabled: false, FromRarity: ItemRarity.Rare));

    /// <summary>The warn gate, or its default when the file said nothing.</summary>
    public EvasionGate WarnOrDefault => Warn ?? Default.Warn!;

    /// <summary>The act gate, on the same terms.</summary>
    public EvasionGate ActOrDefault => Act ?? Default.Act!;

    /// <summary>The movement keys, or W A S D when the file said nothing.</summary>
    public MovementKeys KeysOrDefault => Keys ?? MovementKeys.Default;

    /// <summary>Whether steering can actually run: switched on, and with keys to press.</summary>
    /// <remarks>
    /// Asked in one place because the two halves fail differently and both are silent. Steering
    /// switched on with a movement key unset would hold nothing and let the roll follow the
    /// cursor, which is the OLD behaviour wearing the new switch's clothes.
    /// </remarks>
    public bool CanSteer => Steer && DodgeKey != 0 && KeysOrDefault.IsComplete;

    /// <summary>
    /// Whether the reader has to read monster ACTIONS for any of this to work.
    /// </summary>
    /// <remarks>
    /// The priced setting, asked here for the same reason the tracker asks its own: the reader
    /// and the feature must not be able to disagree about whether the read is happening. Four
    /// reads per hostile monster per tick buys nothing while both gates are off.
    /// </remarks>
    public bool NeedsActions => WarnOrDefault.Enabled || ActOrDefault.Enabled;

    /// <summary>Keeps every value inside what the planner and the overlay can use.</summary>
    public EvasionSettings Normalised() => this with
    {
        Warn = WarnOrDefault.Normalised(),
        Act = ActOrDefault.Normalised(),

        // A radius of zero would mean nothing is ever aimed at you; the upper bound is about a
        // screen's width, beyond which "near the player" has stopped meaning anything.
        DangerRadius = Math.Clamp(DangerRadius, 10f, 2000f),

        // Zero is legal and means "act on every tick that still sees the threat"; the ceiling
        // stops a stray keystroke parking the feature for the rest of the session.
        CooldownMs = Math.Clamp(CooldownMs, 0, 60_000),
        DodgeKey = Math.Clamp(DodgeKey, 0, 0xFF),

        // The floor is a roll that goes somewhere: below it every direction scores the same and
        // the steering would pick whichever came first. The ceiling is well past any roll seen.
        RollDistance = Math.Clamp(RollDistance, 50f, 2000f),

        // Bounded well below the cooldown at both ends. Zero is legal and means "give the keys
        // back at once" - which now defeats the confirmation as well as the hold, since there is
        // no time left in which to notice the roll. It stays legal because it is the honest way
        // to switch the whole wait off.
        SteerHoldMs = Math.Clamp(SteerHoldMs, 0, 500),
        Keys = KeysOrDefault.Normalised(),
        MarkerRadius = Math.Clamp(MarkerRadius, 2f, 200f),
        Thickness = Math.Clamp(Thickness, 0.5f, 10f),
    };
}

/// <summary>Loads and saves the evasion settings beside the executable.</summary>
public static class EvasionSettingsStore
{
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "config", "evasion.json");

    /// <summary>Reads the settings, falling back to the defaults on any problem.</summary>
    /// <remarks>
    /// A corrupt file returns the defaults, which are OFF - the correct way for a settings file
    /// to fail when the setting arms key presses.
    /// </remarks>
    public static EvasionSettings Load(string? path = null)
    {
        string file = path ?? DefaultPath;
        try
        {
            if (!File.Exists(file))
            {
                return EvasionSettings.Default;
            }

            using FileStream stream = File.OpenRead(file);
            EvasionSettings? loaded = JsonSerializer.Deserialize(stream, EvasionJsonContext.Default.EvasionSettings);
            return loaded?.Normalised() ?? EvasionSettings.Default;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return EvasionSettings.Default;
        }
    }

    /// <summary>Writes the settings, returning false when it could not.</summary>
    public static bool Save(EvasionSettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string file = path ?? DefaultPath;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using FileStream stream = File.Create(file);
            JsonSerializer.Serialize(stream, settings, EvasionJsonContext.Default.EvasionSettings);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>Source-generated JSON, so settings survive Native AOT.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(EvasionSettings))]
public sealed partial class EvasionJsonContext : JsonSerializerContext;
