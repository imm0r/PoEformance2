using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoEformance.Features;

/// <summary>One configurable flask slot.</summary>
/// <param name="Slot">Belt slot 1-5.</param>
/// <param name="Enabled">Whether this slot participates at all.</param>
/// <param name="Vital">Which pool its threshold watches.</param>
/// <param name="ThresholdPercent">Fires at or below this fill level of the USABLE pool.</param>
/// <param name="TriggerBuff">Fire on this buff or debuff instead of a threshold, if set.</param>
public sealed record FlaskSlotSettings(
    [property: JsonPropertyName("slot")] int Slot,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("vital")] VitalKind Vital,
    [property: JsonPropertyName("thresholdPercent")] int ThresholdPercent,
    [property: JsonPropertyName("cooldownMs")] int CooldownMs = 1500,
    [property: JsonPropertyName("triggerBuff")] string TriggerBuff = "");

/// <summary>Everything the user can decide about auto-flask.</summary>
/// <remarks>
/// Note what is NOT here: the key. Which key uses a flask is the GAME's setting, read from
/// its config, so duplicating it here would create two sources of truth that silently
/// disagree the moment someone rebinds. It is shown in the UI, not edited there.
/// </remarks>
public sealed record AutoFlaskSettings(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("slots")] IReadOnlyList<FlaskSlotSettings> Slots)
{
    /// <summary>Belt slots in Path of Exile 2 - the number of rows the page shows.</summary>
    public const int SlotCount = 5;

    /// <summary>
    /// Everything present but OFF, with thresholds that are sane if switched on.
    /// </summary>
    /// <remarks>
    /// All five slots are listed rather than only the configured ones, so the UI has a row
    /// per belt slot without inventing entries - and a tool that presses nothing until asked
    /// is the only kind worth shipping.
    /// </remarks>
    public static AutoFlaskSettings Default { get; } = new(
        Enabled: false,
        Slots:
        [
            new FlaskSlotSettings(1, false, VitalKind.Life, 65),
            new FlaskSlotSettings(2, false, VitalKind.Mana, 30),
            new FlaskSlotSettings(3, false, VitalKind.Life, 50),
            new FlaskSlotSettings(4, false, VitalKind.Life, 50),
            new FlaskSlotSettings(5, false, VitalKind.Life, 50),
        ]);

    /// <summary>
    /// Returns these settings with exactly one entry per belt slot and every value in range.
    /// </summary>
    /// <remarks>
    /// The file is meant to be hand-editable, which means it can arrive holding a threshold
    /// of 5000, a missing slot, or two entries for slot 2. None of those should reach the
    /// engine: a threshold above 100 fires on a full globe forever, and a missing slot would
    /// leave the page a row short of the belt it is describing. Applied on load AND on every
    /// change from the page, so there is one place where the settings become trustworthy.
    /// </remarks>
    public AutoFlaskSettings Normalised()
    {
        var slots = new List<FlaskSlotSettings>(SlotCount);
        for (int slot = 1; slot <= SlotCount; slot++)
        {
            FlaskSlotSettings source = Find(slot) ?? Default.Slots[slot - 1];
            slots.Add(source with
            {
                Slot = slot,
                ThresholdPercent = Math.Clamp(source.ThresholdPercent, 1, 100),

                // A zero cooldown is legal and means "the buff check is the only guard";
                // the upper bound is only there to stop a stray keystroke parking a flask
                // for the rest of the session.
                CooldownMs = Math.Clamp(source.CooldownMs, 0, 60_000),
                TriggerBuff = source.TriggerBuff ?? string.Empty,
            });
        }

        return this with { Slots = slots };
    }

    /// <summary>The first entry for a belt slot, or null when the file has none.</summary>
    private FlaskSlotSettings? Find(int slot)
    {
        foreach (FlaskSlotSettings candidate in Slots)
        {
            if (candidate.Slot == slot)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Turns the settings plus the game's key bindings into engine rules.</summary>
    public IReadOnlyList<FlaskRule> ToRules(FlaskKeys bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);

        var rules = new List<FlaskRule>();
        foreach (FlaskSlotSettings slot in Slots)
        {
            bindings.BySlot.TryGetValue(slot.Slot, out ushort key);
            rules.Add(new FlaskRule(
                $"Slot {slot.Slot}",
                slot.Vital,
                slot.ThresholdPercent,
                key,
                slot.CooldownMs)
            {
                // An unmapped key means the rule can do nothing, so it stays off however it
                // was configured - better than pressing something the player never bound.
                Enabled = slot.Enabled && key != 0,
                Slot = slot.Slot,
                TriggerBuff = slot.TriggerBuff,
            });
        }

        return rules;
    }
}

/// <summary>Loads and saves the settings next to the executable.</summary>
/// <remarks>
/// Beside the executable rather than in the user profile, matching the offsets schema and
/// the UI folder: the tool is a portable directory and its state should travel with it.
/// </remarks>
public static class AutoFlaskSettingsStore
{
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "config", "autoflask.json");

    /// <summary>Reads the settings, falling back to the defaults on any problem.</summary>
    /// <remarks>
    /// A corrupt or hand-edited file returns the defaults rather than throwing: everything
    /// here is off by default, so the failure mode is a tool that does nothing - which is
    /// the correct way for a settings file to fail when the setting arms key presses.
    /// </remarks>
    public static AutoFlaskSettings Load(string? path = null)
    {
        string file = path ?? DefaultPath;
        try
        {
            if (!File.Exists(file))
            {
                return AutoFlaskSettings.Default;
            }

            using FileStream stream = File.OpenRead(file);
            AutoFlaskSettings? loaded = JsonSerializer.Deserialize(stream, AutoFlaskJsonContext.Default.AutoFlaskSettings);
            return loaded?.Normalised() ?? AutoFlaskSettings.Default;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return AutoFlaskSettings.Default;
        }
    }

    /// <summary>Writes the settings, returning false when it could not.</summary>
    public static bool Save(AutoFlaskSettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string file = path ?? DefaultPath;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using FileStream stream = File.Create(file);
            JsonSerializer.Serialize(stream, settings, AutoFlaskJsonContext.Default.AutoFlaskSettings);
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
[JsonSerializable(typeof(AutoFlaskSettings))]
public sealed partial class AutoFlaskJsonContext : JsonSerializerContext;
