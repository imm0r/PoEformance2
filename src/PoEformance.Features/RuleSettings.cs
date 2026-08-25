using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoEformance.Features;

/// <summary>One rule: when it applies, and what it does.</summary>
/// <param name="Id">
/// Stable identity, and the thing the reference plugin does without.
/// </param>
/// <param name="Priority">Higher wins when several fire at once.</param>
/// <param name="AllowLower">
/// Whether rules below this one may also act this tick. Clearing it on a high-priority rule is
/// how "when this is happening, nothing else matters" is expressed.
/// </param>
/// <remarks>
/// <paramref name="Id"/> is what cooldowns and interval timers are kept under. The reference
/// plugin keys both on the rule's NAME, so two rules called "New rule" - which is what its own
/// add button produces - share one cooldown, and renaming a rule silently hands it a fresh one
/// mid-fight.
/// </remarks>
public sealed record Rule(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("condition")] RuleCondition Condition,
    [property: JsonPropertyName("effects")] IReadOnlyList<RuleEffect> Effects)
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("priority")]
    public int Priority { get; init; }

    [JsonPropertyName("allowLower")]
    public bool AllowLower { get; init; } = true;

    /// <summary>What the rule is for, in the author's own words.</summary>
    [JsonPropertyName("comment")]
    public string Comment { get; init; } = string.Empty;

    /// <summary>
    /// The node graph this rule was drawn as, when it was drawn rather than typed.
    /// </summary>
    /// <remarks>
    /// A rule has ONE source, and this is it whenever it is present: <see cref="Normalised"/>
    /// derives <see cref="Condition"/> from the graph, so the two can never disagree about
    /// what the rule asks. Null means the rule was written as text, and then the condition
    /// stands on its own; the editor draws a graph from the tree on demand and only stores one
    /// once somebody moves a box.
    ///
    /// This is a correction rather than the original design. The graph was described as
    /// LAYOUT and nothing derived a condition from it, so a rule built entirely in the canvas
    /// kept the empty condition it was created with - which says nothing, and therefore fires
    /// nothing. Every box somebody wired up was saved faithfully and evaluated not at all.
    ///
    /// The cost of the correction, stated because the file is hand-editable: on a rule that
    /// carries a graph, editing `condition` in the file does nothing - the next load derives
    /// it again. Editing the graph, or switching the rule to text in the editor, is how that
    /// rule's logic changes.
    /// </remarks>
    [JsonPropertyName("graph")]
    public RuleGraph? Graph { get; init; }

    /// <summary>Brings a rule from a file or a page into a state the engine can run.</summary>
    public Rule Normalised()
    {
        var effects = new List<RuleEffect>();
        foreach (RuleEffect effect in Effects ?? [])
        {
            if (effects.Count == MaxEffects)
            {
                break;
            }

            effects.Add(effect.Normalised());
        }

        // A drawn rule's boxes ARE its logic, so the tree is derived from them here - the one
        // place a rule becomes what the engine runs, reached by the file and by the config
        // page alike. Without this the canvas is a drawing: saved faithfully, evaluated never.
        RuleCondition condition = Graph is RuleGraph drawn
            ? drawn.ToCondition()
            : Condition ?? new RuleCondition { Kind = ConditionKind.All };

        return this with
        {
            // An id is what everything about this rule is remembered under, so one is minted
            // rather than the rule being dropped: a hand-written file should not need to
            // invent GUIDs to be loadable.
            Id = string.IsNullOrWhiteSpace(Id) ? NewId() : Id.Trim(),
            Name = string.IsNullOrWhiteSpace(Name) ? "Rule" : Name.Trim(),
            Comment = Comment ?? string.Empty,
            Condition = condition.Trimmed(),
            Effects = effects,
        };
    }

    /// <summary>How many effects one rule may carry.</summary>
    public const int MaxEffects = 8;

    /// <summary>A fresh identity for a rule the editor just added.</summary>
    public static string NewId() => Guid.NewGuid().ToString("n");
}

/// <summary>A set of rules that share a switch and a place they apply.</summary>
/// <remarks>
/// The three area flags are the reference plugin's, and they earn their place: the rules worth
/// running while mapping are almost never the ones worth running in a hideout, and without
/// this every rule needs an InTown of its own bolted onto its condition.
/// </remarks>
public sealed record RuleGroup(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("rules")] IReadOnlyList<Rule> Rules)
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("inTown")]
    public bool InTown { get; init; }

    [JsonPropertyName("inHideout")]
    public bool InHideout { get; init; }

    [JsonPropertyName("inMaps")]
    public bool InMaps { get; init; } = true;

    /// <summary>Whether this group's rules run where the player currently is.</summary>
    public bool AppliesIn(RuleState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!Enabled)
        {
            return false;
        }

        if (state.InTown)
        {
            return InTown;
        }

        return state.InHideout ? InHideout : InMaps;
    }

    public RuleGroup Normalised()
    {
        var rules = new List<Rule>();
        foreach (Rule rule in Rules ?? [])
        {
            if (rules.Count == MaxRules)
            {
                break;
            }

            rules.Add(rule.Normalised());
        }

        return this with
        {
            Name = string.IsNullOrWhiteSpace(Name) ? "Group" : Name.Trim(),
            Rules = rules,
        };
    }

    /// <summary>How many rules one group may hold.</summary>
    /// <remarks>
    /// Every rule in an applying group is evaluated on every tick, so this is a bound on how
    /// much work one edited file can ask of the reader thread.
    /// </remarks>
    public const int MaxRules = 256;
}

/// <summary>A whole set of groups, switched to as one.</summary>
public sealed record RuleProfile(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("groups")] IReadOnlyList<RuleGroup> Groups)
{
    public RuleProfile Normalised()
    {
        var groups = new List<RuleGroup>();
        foreach (RuleGroup group in Groups ?? [])
        {
            if (groups.Count == MaxGroups)
            {
                break;
            }

            groups.Add(group.Normalised());
        }

        return this with
        {
            Name = string.IsNullOrWhiteSpace(Name) ? "Profile" : Name.Trim(),
            Groups = groups,
        };
    }

    public const int MaxGroups = 64;
}

/// <summary>Everything the user can decide about the rule engine.</summary>
public sealed record RuleSettings(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("profile")] string Profile,
    [property: JsonPropertyName("profiles")] IReadOnlyList<RuleProfile> Profiles)
{
    /// <summary>
    /// Whether a rule may make itself noticed while the game is behind another window.
    /// </summary>
    /// <remarks>
    /// Captions AND cues, which is why it is not called DrawInBackground. A sound is the half
    /// that reaches somebody who is not looking at the screen, so a rule beeping about a rare
    /// monster goes on beeping into whatever they alt-tabbed to - and unlike a caption drawn
    /// somewhere they cannot see, that is impossible to ignore.
    ///
    /// What switching it ON actually buys is captions while THIS TOOL'S own windows have
    /// focus, which is the moment somebody is tuning a rule and wants to see it fire. It
    /// cannot draw anything while a browser is in front: the overlay covers the game's client
    /// area and refuses to paint when neither the game nor one of our windows is foreground.
    /// That rule is the overlay's and this does not reach it. A CUE has no such second gate,
    /// so this switch is the only thing standing between a rule and somebody else's meeting.
    ///
    /// Input is not covered either way. It is gated on focus in the engine and no setting
    /// reaches that gate, because the gate is not a preference - keystrokes land wherever
    /// focus is.
    /// </remarks>
    [JsonPropertyName("noticeInBackground")]
    public bool NoticeInBackground { get; init; }

    /// <summary>Shortest gap between any two synthesised inputs, whichever rules asked.</summary>
    /// <remarks>
    /// A floor under every per-rule cooldown, so a profile with six rules each set to fire
    /// freely cannot turn into a stream of keystrokes. The per-rule cooldown says how often ONE
    /// rule may act; this says how fast the tool may type.
    /// </remarks>
    [JsonPropertyName("minInputGapMs")]
    public int MinInputGapMs { get; init; } = 100;

    /// <summary>Nothing armed, and one empty profile to start from.</summary>
    /// <remarks>
    /// Off, and with no rules in it. A tool that presses nothing until asked is the only kind
    /// worth shipping - the same first-run rule auto-flask follows - and it matters more here,
    /// where a shipped example could be armed with a key press.
    /// </remarks>
    public static RuleSettings Default { get; } = new(
        Enabled: false,
        Profile: "Default",
        Profiles: [new RuleProfile("Default", [])]);

    /// <summary>The profile currently selected, or the first one when the name is stale.</summary>
    public RuleProfile? Current
    {
        get
        {
            foreach (RuleProfile profile in Profiles)
            {
                if (string.Equals(profile.Name, Profile, StringComparison.Ordinal))
                {
                    return profile;
                }
            }

            return Profiles.Count > 0 ? Profiles[0] : null;
        }
    }

    /// <summary>Brings the settings into a state the engine can run.</summary>
    public RuleSettings Normalised()
    {
        var profiles = new List<RuleProfile>();
        foreach (RuleProfile profile in Profiles ?? [])
        {
            if (profiles.Count == MaxProfiles)
            {
                break;
            }

            profiles.Add(profile.Normalised());
        }

        if (profiles.Count == 0)
        {
            profiles.Add(new RuleProfile("Default", []));
        }

        string selected = Profile ?? string.Empty;
        if (!profiles.Exists(profile => string.Equals(profile.Name, selected, StringComparison.Ordinal)))
        {
            selected = profiles[0].Name;
        }

        return this with
        {
            Profile = selected,
            Profiles = profiles,
            MinInputGapMs = Math.Clamp(MinInputGapMs, 0, 10_000),
        };
    }

    public const int MaxProfiles = 32;
}

/// <summary>Loads and saves the rules next to the executable.</summary>
/// <remarks>
/// Beside the executable, like the offsets schema and the auto-flask settings: the tool is a
/// portable directory and its state travels with it.
/// </remarks>
public static class RuleSettingsStore
{
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "config", "rules.json");

    /// <summary>Reads the rules, falling back to the defaults on any problem.</summary>
    /// <remarks>
    /// A corrupt file loads as the defaults rather than throwing, and the defaults are off - so
    /// the failure mode of a settings file that arms key presses is a tool that does nothing.
    /// </remarks>
    public static RuleSettings Load(string? path = null)
    {
        string file = path ?? DefaultPath;
        try
        {
            if (!File.Exists(file))
            {
                return RuleSettings.Default;
            }

            using FileStream stream = File.OpenRead(file);
            RuleSettings? loaded = JsonSerializer.Deserialize(stream, RuleJsonContext.Default.RuleSettings);
            return loaded?.Normalised() ?? RuleSettings.Default;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return RuleSettings.Default;
        }
    }

    /// <summary>Writes the rules, returning false when it could not.</summary>
    /// <remarks>
    /// Through a temporary file and a move, which the reference plugin also does and for a
    /// reason worth keeping: a profile is a lot of work to lose, and a write interrupted in
    /// place leaves a file that parses as far as the cut and then does not.
    /// </remarks>
    public static bool Save(RuleSettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string file = path ?? DefaultPath;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temporary = file + ".new";

            using (FileStream stream = File.Create(temporary))
            {
                JsonSerializer.Serialize(stream, settings, RuleJsonContext.Default.RuleSettings);
            }

            File.Move(temporary, file, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>Source-generated JSON, so the rules survive Native AOT.</summary>
/// <remarks>
/// Reflection-based serialisation is exactly the dependency that dies silently under AOT, and
/// a condition tree is recursive - which the generator handles and a hand-written reader would
/// be the wrong place to discover.
/// </remarks>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(RuleSettings))]
public sealed partial class RuleJsonContext : JsonSerializerContext;
