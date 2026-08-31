using System.Text.Json;
using System.Text.Json.Serialization;
using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>
/// Lines drawn from the player to the monsters worth knowing about, by rarity.
/// </summary>
/// <remarks>
/// The cheapest thing in this file and the one that survives a crowded screen: a rare pack
/// leader behind a wall of its own minions is invisible as a dot and obvious as a line, because
/// a line has one end where the eye already is.
///
/// One switch and one colour per rarity rather than a single switch with a colour ramp - the
/// three are wanted at different times. Uniques all map long, rares in a breach, magic almost
/// never.
/// </remarks>
public sealed record MonsterLineSettings(
    [property: JsonPropertyName("unique")] bool Unique = false,
    [property: JsonPropertyName("rare")] bool Rare = false,
    [property: JsonPropertyName("magic")] bool Magic = false,
    [property: JsonPropertyName("uniqueColour")] string UniqueColour = "#90FF9400",
    [property: JsonPropertyName("rareColour")] string RareColour = "#7DFFFC00",
    [property: JsonPropertyName("magicColour")] string MagicColour = "#4B001EFF")
{
    public static MonsterLineSettings Default { get; } = new();

    /// <summary>Whether lines are drawn for a rarity at all, and in which colour.</summary>
    /// <remarks>
    /// Asked as one question because the answer is used as one: a colour without the switch
    /// draws lines nobody asked for, and a switch without the colour draws them invisible.
    /// </remarks>
    public (bool On, string Colour) For(ItemRarity rarity) => rarity switch
    {
        ItemRarity.Unique => (Unique, UniqueColour),
        ItemRarity.Rare => (Rare, RareColour),
        ItemRarity.Magic => (Magic, MagicColour),
        _ => (false, ""),
    };
}

/// <summary>
/// A patch of ground worth staying off, recognised by the start of its metadata path.
/// </summary>
/// <remarks>
/// WHY A PATH PREFIX AND NOT A LIST OF NAMES. What the game calls a damaging ground effect is
/// not written down anywhere this tool can read, and it differs per skill, per league mechanic
/// and per boss. A prefix is what a person can arrive at by looking at the Effects tab, typing
/// the part before the number, and seeing a circle appear - which is the only workflow that
/// works for a thing nobody has a list of.
///
/// <see cref="Radius"/> IS IN SCREEN PIXELS, not world units, and that is worth stating because
/// it looks like the other one. It comes straight from the reference plugin, whose own defaults
/// (50-300) are pixel figures; a world-unit radius would have to be projected as a second point
/// and would shrink with the camera, which is a different feature and not this one.
/// </remarks>
public sealed record GroundDangerRule(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("enabled")] bool Enabled = true,
    [property: JsonPropertyName("colour")] string Colour = "#99FF4D00",
    [property: JsonPropertyName("radius")] int Radius = 100,
    [property: JsonPropertyName("thickness")] int Thickness = 2,
    [property: JsonPropertyName("filled")] bool Filled = false)
{
    /// <summary>Whether this rule says anything at all about what to match.</summary>
    /// <remarks>
    /// An empty prefix matches EVERY entity in the area, which would ring the whole map. It
    /// arrives from the editor's own "add" button, so it is checked rather than trusted.
    /// </remarks>
    public bool SaysNothing => string.IsNullOrWhiteSpace(Path);

    /// <summary>Whether this entity is the sort of ground this rule is about.</summary>
    /// <remarks>
    /// FRIENDLY ONES ARE NEVER MARKED, from the reference and for a reason worth keeping: your
    /// own skills leave ground effects too, and a ring around every patch of your own burning
    /// ground is the overlay covering the screen at exactly the moment it is needed.
    /// </remarks>
    public bool Matches(WorldEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return Enabled
               && !SaysNothing
               && !entity.IsFriendly
               && entity.Path.StartsWith(Path.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// A buff or debuff worth drawing over the thing carrying it.
/// </summary>
/// <remarks>
/// MATCHED AS A SUBSTRING, CASE-INSENSITIVELY, where the reference matches the game's key
/// exactly. Two reasons, and the second is the one that decides it. The names are internal
/// spellings - "shocked_70", "stolen_mods_buff_70" - so an exact match means the feature does
/// nothing at all until somebody has found the exact string; and this codebase already answers
/// the same question the same way for flasks (<see cref="ActiveBuffs.Has"/>), so a second rule
/// would be a second thing to explain. The shipped rules keep the reference's full names, which
/// match either way, and the tab lists what is actually on the player so a name can be copied
/// rather than guessed.
/// </remarks>
/// <param name="Name">Part of the game's own buff name. Empty matches nothing.</param>
/// <param name="Label">What to call it on screen when there is no icon to draw.</param>
/// <param name="IconColumn">Column in the icon sheet, counted in tiles from the left.</param>
/// <param name="IconRow">Row in the icon sheet, counted in tiles from the top.</param>
public sealed record StatusIconRule(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("label")] string Label = "",
    [property: JsonPropertyName("enabled")] bool Enabled = true,
    [property: JsonPropertyName("textColour")] string TextColour = "#FFFFFFFF",
    [property: JsonPropertyName("barColour")] string BarColour = "#FF740808",
    [property: JsonPropertyName("iconColumn")] int IconColumn = 0,
    [property: JsonPropertyName("iconRow")] int IconRow = 0,
    [property: JsonPropertyName("iconScale")] float IconScale = 1f)
{
    /// <summary>Whether this rule says anything at all about what to match.</summary>
    public bool SaysNothing => string.IsNullOrWhiteSpace(Name);

    /// <summary>Whether a buff of this name is the one this rule is about.</summary>
    public bool Matches(string buffName)
        => Enabled
           && !SaysNothing
           && !string.IsNullOrEmpty(buffName)
           && buffName.Contains(Name.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>What to write when there is no icon: the label, else the name.</summary>
    public string Caption => Label.Length > 0 ? Label : Name;
}

/// <summary>Where a row of status icons sits relative to the thing it belongs to.</summary>
/// <param name="X">Sideways from the entity's own position, in pixels.</param>
/// <param name="Y">
/// Up or down from it. NEGATIVE is up the screen, which is why the monster default is negative
/// and the player's is not: a monster's icons go above its head, and the player's go below the
/// character so they do not sit under the cursor.
/// </param>
/// <param name="Gap">Pixels between two icons in the row.</param>
public sealed record StatusIconLayout(
    [property: JsonPropertyName("x")] int X = 0,
    [property: JsonPropertyName("y")] int Y = 0,
    [property: JsonPropertyName("gap")] int Gap = 4);

/// <summary>
/// The offsets that suit one screen height, so nobody has to find them by dragging sliders.
/// </summary>
/// <remarks>
/// The icons are drawn at a fixed pixel size while the game's characters are drawn larger on a
/// taller screen, so the gap between a monster's feet and the space above its head is not the
/// same number of pixels at 1080p and at 4K. The reference plugin ships three sets of figures
/// measured against real screens; they are kept here as data, applied by height at first run,
/// and overridable afterwards.
/// </remarks>
public sealed record StatusLayoutProfile(string Name, StatusIconLayout Player, StatusIconLayout Monster)
{
    /// <summary>The measured profiles, shortest screen first.</summary>
    public static IReadOnlyList<StatusLayoutProfile> All { get; } =
    [
        new("1080p", new StatusIconLayout(0, 30, 3), new StatusIconLayout(0, -34, 3)),
        new("1440p", new StatusIconLayout(0, 38, 4), new StatusIconLayout(0, -42, 4)),
        new("4K", new StatusIconLayout(0, 52, 5), new StatusIconLayout(0, -58, 5)),
    ];

    /// <summary>The profile that suits a screen of this height.</summary>
    /// <remarks>
    /// Rounded DOWN to the nearest measured screen, so an unusual height lands on the profile
    /// below it rather than on none. An ultrawide 3440x1440 is a 1440p screen for this purpose,
    /// which is the whole reason the choice is made on height rather than on a resolution name.
    /// </remarks>
    public static StatusLayoutProfile ForHeight(float screenHeight) => screenHeight switch
    {
        >= 2160 => All[2],
        >= 1440 => All[1],
        _ => All[0],
    };
}

/// <summary>
/// The line showing where a monster is pointing, and what it is doing while it points there.
/// </summary>
/// <remarks>
/// WHAT THIS ANSWERS AND WHAT IT CANNOT. The game keeps no target pointer - it aims by turning
/// the actor to an angle and firing once it is within a tolerance - so a ray along the facing IS
/// the aim, as exactly as the game itself has it. What it is NOT is a promise about where a
/// skill will land: many attacks lock their direction at the moment they start and then the
/// monster turns away, and a homing projectile ignores the facing entirely. The ray says where
/// the monster is pointing right now, which is what it knows.
///
/// <see cref="OnlyWhileActing"/> is what makes it usable rather than a screen of lines: every
/// monster in the area faces somewhere at all times, and almost none of them are about to do
/// anything about it.
/// </remarks>
/// <param name="Length">How far the ray runs, in WORLD units - so it shrinks with the camera.</param>
/// <param name="OnlyWhileActing">
/// Draw only for monsters whose animation is not idling or walking. Unknown animations COUNT AS
/// acting: the name table has 1084 entries and the game has more, and a marker that vanishes
/// reads as "nothing is happening".
/// </param>
/// <param name="ShowTurn">
/// Also draw the angle it is turning TO, when that differs from where it points. This is the
/// aim proper, a step ahead of the pose, and it is only ever on screen for the length of a turn
/// - a median of 94 ms.
/// </param>
public sealed record AimSettings(
    [property: JsonPropertyName("enabled")] bool Enabled = false,
    [property: JsonPropertyName("length")] float Length = 60f,
    [property: JsonPropertyName("thickness")] float Thickness = 2f,
    [property: JsonPropertyName("colour")] string Colour = "#B4FF6432",
    [property: JsonPropertyName("turnColour")] string TurnColour = "#B4FFC800",
    [property: JsonPropertyName("onlyWhileActing")] bool OnlyWhileActing = true,
    [property: JsonPropertyName("showTurn")] bool ShowTurn = true,
    [property: JsonPropertyName("showAction")] bool ShowAction = false,
    [property: JsonPropertyName("showPlayer")] bool ShowPlayer = false)
{
    public static AimSettings Default { get; } = new();

    /// <summary>Keeps the values inside what can be drawn.</summary>
    public AimSettings Normalised() => this with
    {
        Length = Math.Clamp(Length, 5f, 400f),
        Thickness = Math.Clamp(Thickness, 0.5f, 10f),
    };
}

/// <summary>
/// What the tracker draws: lines to monsters, rings on dangerous ground, status icons.
/// </summary>
/// <remarks>
/// PORTED FROM the GameHelper2 plugin <c>hyper911/Tracker-GH2</c> (MIT), whose three logic
/// classes - MonsterLineLogic, GroundEffectLogic, StatusEffectLogic - become the three sections
/// here and three layers in the overlay. They travel in ONE settings file because they are one
/// decision: what is happening around me right now.
///
/// WHAT WAS DELIBERATELY NOT PORTED, so nobody goes looking for it. The plugin's import/export
/// buttons and its 28-to-8 column icon remap are its own upgrade history - the remap converts
/// coordinates saved against a sheet layout that only ever existed there - and the "sync ground
/// effects from an external file" switch reads another copy of the plugin's own settings file.
/// None of the three means anything outside that plugin's installation.
/// </remarks>
public sealed record TrackerSettings(
    [property: JsonPropertyName("lines")] MonsterLineSettings? Lines = null,
    [property: JsonPropertyName("groundDanger")] IReadOnlyList<GroundDangerRule>? GroundDanger = null,
    [property: JsonPropertyName("showGroundDanger")] bool ShowGroundDanger = true,

    // ── The hazards the game names itself ────────────────────────────────────
    // Separate switches from the rules above, and separate on purpose: a rule matches a path
    // somebody thought to type, while these two draw whatever CARRIES THE COMPONENT. That is
    // the difference between a list to maintain and a fact, and it is why they can also show a
    // countdown and a direction, which no rule could.
    [property: JsonPropertyName("showGroundEffects")] bool ShowGroundEffects = false,
    [property: JsonPropertyName("groundEffectColour")] string GroundEffectColour = "#C8FF7A1E",
    // WORLD units, not screen pixels - the ring is a circle on the floor, projected, so it
    // foreshortens and shrinks with the camera exactly as the ground does. Only used when the
    // game's own candidate value is switched off or unreadable.
    [property: JsonPropertyName("groundEffectRadius")] float GroundEffectRadius = 20f,
    [property: JsonPropertyName("groundEffectUseGameRadius")] bool GroundEffectUseGameRadius = true,
    [property: JsonPropertyName("showGroundEffectTimer")] bool ShowGroundEffectTimer = true,

    // The metadata path and the numbers under each ring. A DEBUG AID: it answers "which patch
    // is that ring on, and is one missing", which circles alone cannot. Off by default because
    // it writes a 56-character path under every effect on screen.
    [property: JsonPropertyName("showGroundEffectLabels")] bool ShowGroundEffectLabels = false,
    [property: JsonPropertyName("showBeams")] bool ShowBeams = false,
    [property: JsonPropertyName("beamColour")] string BeamColour = "#E6FF6A2A",

    // How wide the band is drawn, in screen pixels. A DISPLAY CHOICE that carries no claim
    // about how far the beam reaches sideways - see BeamLayer. Wide enough by default to be
    // glanceable during a fight, which is the whole reason it is a band and not a line.
    [property: JsonPropertyName("beamThickness")] float BeamThickness = 24f,
    [property: JsonPropertyName("monsterStatus")] IReadOnlyList<StatusIconRule>? MonsterStatus = null,
    [property: JsonPropertyName("playerStatus")] IReadOnlyList<StatusIconRule>? PlayerStatus = null,
    [property: JsonPropertyName("showMonsterStatus")] bool ShowMonsterStatus = false,
    [property: JsonPropertyName("showPlayerStatus")] bool ShowPlayerStatus = false,
    [property: JsonPropertyName("monsterLayout")] StatusIconLayout? MonsterLayout = null,
    [property: JsonPropertyName("playerLayout")] StatusIconLayout? PlayerLayout = null,
    [property: JsonPropertyName("layoutChosen")] bool LayoutChosen = false,
    [property: JsonPropertyName("iconSheet")] string IconSheet = "",
    [property: JsonPropertyName("iconTile")] int IconTile = 32,
    [property: JsonPropertyName("shadowAlpha")] float ShadowAlpha = 0.5f,
    [property: JsonPropertyName("shadowSize")] int ShadowSize = 1,
    [property: JsonPropertyName("barBackColour")] string BarBackColour = "#AA000000",
    [property: JsonPropertyName("chargesX")] int ChargesX = 0,
    [property: JsonPropertyName("chargesY")] int ChargesY = 0,
    [property: JsonPropertyName("timerX")] int TimerX = 0,
    [property: JsonPropertyName("timerY")] int TimerY = 0,
    [property: JsonPropertyName("aim")] AimSettings? Aim = null)
{
    public static TrackerSettings Default { get; } = new();

    /// <summary>The monster lines, or their defaults when the file said nothing.</summary>
    public MonsterLineSettings LinesOrDefault => Lines ?? MonsterLineSettings.Default;

    /// <summary>
    /// The ground rules to draw - the shipped one when the file never said.
    /// </summary>
    /// <remarks>
    /// ABSENT takes the default and EMPTY is honoured, the same distinction the alerts make:
    /// deleting every rule has to be something a person can do, and a list that grows its
    /// defaults back is a list that cannot be emptied.
    /// </remarks>
    public IReadOnlyList<GroundDangerRule> GroundDangerOrDefault => GroundDanger ?? DefaultTracker.GroundDanger;

    /// <summary>The monster status rules, on the same terms.</summary>
    public IReadOnlyList<StatusIconRule> MonsterStatusOrDefault => MonsterStatus ?? DefaultTracker.MonsterStatus;

    /// <summary>The player status rules, on the same terms.</summary>
    public IReadOnlyList<StatusIconRule> PlayerStatusOrDefault => PlayerStatus ?? DefaultTracker.PlayerStatus;

    /// <summary>Whether the reader has to read a monster's buffs for any of this to draw.</summary>
    /// <remarks>
    /// The one setting here with a price on it: a monster's buffs are a component read per
    /// monster that nothing else in the tool needs, so the reader only pays it while this is
    /// switched on AND a rule is enabled. Asked here rather than in the overlay so the reader
    /// and the layer cannot disagree about it.
    /// </remarks>
    public bool NeedsMonsterBuffs
        => ShowMonsterStatus && MonsterStatusOrDefault.Any(rule => rule.Enabled && !rule.SaysNothing);

    /// <summary>The aim settings, or their defaults when the file said nothing.</summary>
    public AimSettings AimOrDefault => Aim ?? AimSettings.Default;

    /// <summary>Whether the reader has to read facing and animation for anything to draw.</summary>
    /// <remarks>
    /// The second of the two priced settings, and asked here for the same reason as the first:
    /// the reader and the layer must not be able to disagree about whether the read is happening.
    /// </remarks>
    public bool NeedsAim => AimOrDefault.Enabled;

    /// <summary>Keeps every value inside the range the overlay can draw.</summary>
    /// <remarks>
    /// A hand-edited file is the normal source of these, and every one of them has a way of
    /// being wrong that is invisible: a tile size of 0 divides by zero in the UV arithmetic, a
    /// scale of 100 draws one icon over the whole screen, and a rule with no name matches
    /// everything.
    /// </remarks>
    public TrackerSettings Normalised() => this with
    {
        GroundDanger = GroundDanger is null
            ? null
            : [.. GroundDanger.Where(rule => rule is not null && !rule.SaysNothing)],
        MonsterStatus = Cleaned(MonsterStatus),
        PlayerStatus = Cleaned(PlayerStatus),

        // Never zero: the icon sheet's tile size is a DIVISOR in the UV arithmetic.
        IconTile = Math.Clamp(IconTile, 1, 512),
        ShadowAlpha = Math.Clamp(ShadowAlpha, 0f, 1f),
        ShadowSize = Math.Clamp(ShadowSize, 0, 2),
        Aim = Aim?.Normalised(),
    };

    private static IReadOnlyList<StatusIconRule>? Cleaned(IReadOnlyList<StatusIconRule>? rules)
        => rules is null
            ? null
            : [.. rules
                .Where(rule => rule is not null && !rule.SaysNothing)
                .Select(rule => rule with
                {
                    IconColumn = Math.Max(0, rule.IconColumn),
                    IconRow = Math.Max(0, rule.IconRow),
                    IconScale = Math.Clamp(rule.IconScale, 0.2f, 10f),
                })];
}

/// <summary>What one status rule found on an entity: the rule, and the buff that matched it.</summary>
/// <remarks>
/// The rule and the reading travel together because both halves are needed to draw one icon -
/// the rule says what it looks like, the buff says how long is left and how many stacks. Kept
/// out of the overlay so the matching can be tested off Windows, which is where the tests run.
/// </remarks>
/// <param name="TimeLeft">Seconds remaining, 0 when the game reports none.</param>
/// <param name="TotalTime">
/// What it started at, for the proportion the timer bar fills. 0 means the game did not say,
/// and the bar is drawn full rather than empty - an unknown duration is not an expired one.
/// </param>
public readonly record struct StatusIconHit(StatusIconRule Rule, float TimeLeft, float TotalTime, int Charges)
{
    /// <summary>How much of the timer bar to fill, 0..1.</summary>
    public float Proportion
        => TotalTime > 0.0001f ? Math.Clamp(TimeLeft / TotalTime, 0f, 1f) : 1f;

    /// <summary>The remaining time as the overlay writes it: <c>m:ss</c>.</summary>
    public string Timer
    {
        get
        {
            int seconds = Math.Max(0, (int)Math.Floor(TimeLeft));
            return $"{seconds / 60}:{seconds % 60:00}";
        }
    }
}

/// <summary>Which of the rules are currently on a thing, in the order they are drawn.</summary>
public static class StatusIcons
{
    /// <summary>Most icons drawn over one entity. A backstop, not a preference.</summary>
    /// <remarks>
    /// A row of icons is centred on the entity, so an unbounded one grows across the whole
    /// screen in both directions. Nothing sane reaches this - it takes twelve enabled rules all
    /// matching one monster.
    /// </remarks>
    public const int MostIcons = 12;

    /// <summary>The rules that match what is on this entity right now.</summary>
    /// <remarks>
    /// IN RULE ORDER rather than in the game's buff order, so the row on screen does not
    /// reshuffle itself as buffs come and go. A moving icon is one that has to be read again
    /// every time it is looked at.
    ///
    /// One hit per RULE, taking the first buff that matches it: two rules can want the same
    /// buff (one for the icon, one for a louder colour) and that is the user's business, but
    /// one rule matching three stacks of the same debuff has to be one icon.
    /// </remarks>
    public static IReadOnlyList<StatusIconHit> On(IReadOnlyList<StatusIconRule> rules, ActiveBuffs? buffs)
    {
        ArgumentNullException.ThrowIfNull(rules);

        if (buffs is null || buffs.All.Count == 0)
        {
            return [];
        }

        var found = new List<StatusIconHit>();
        foreach (StatusIconRule rule in rules)
        {
            if (!rule.Enabled || rule.SaysNothing)
            {
                continue;
            }

            foreach (ActiveBuff buff in buffs.All)
            {
                if (!rule.Matches(buff.Name))
                {
                    continue;
                }

                found.Add(new StatusIconHit(rule, buff.TimeLeft, buff.TotalTime, buff.Charges));
                break;
            }

            if (found.Count >= MostIcons)
            {
                break;
            }
        }

        return found;
    }
}

/// <summary>The rules a fresh install starts with.</summary>
/// <remarks>
/// The reference plugin's own shipped entries, names included. They are examples rather than a
/// list anybody should be stuck with - one ground effect, one monster debuff, one player buff -
/// and they exist so that switching the feature on draws something, which is the only way to
/// find out that it works.
/// </remarks>
public static class DefaultTracker
{
    public static IReadOnlyList<GroundDangerRule> GroundDanger { get; } =
    [
        new("Metadata/Effects/Spells/ground_effects/VisibleServerGroundEffect"),
    ];

    public static IReadOnlyList<StatusIconRule> MonsterStatus { get; } =
    [
        new("shocked_70", "Shocked", TextColour: "#FF7EFF00", BarColour: "#FF002C5E"),
    ];

    public static IReadOnlyList<StatusIconRule> PlayerStatus { get; } =
    [
        new("stolen_mods_buff_70", "Stolen buff", TextColour: "#FF00FF78", BarColour: "#A93DB6FF"),
    ];
}

/// <summary>Loads and saves the tracker settings next to the executable.</summary>
public static class TrackerStore
{
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "config", "tracker.json");

    /// <summary>Reads the settings, falling back to the defaults on any problem.</summary>
    public static TrackerSettings Load(string? path = null)
    {
        string file = path ?? DefaultPath;
        try
        {
            if (!File.Exists(file))
            {
                return TrackerSettings.Default;
            }

            using FileStream stream = File.OpenRead(file);
            TrackerSettings? loaded = JsonSerializer.Deserialize(stream, TrackerJsonContext.Default.TrackerSettings);
            return loaded?.Normalised() ?? TrackerSettings.Default;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return TrackerSettings.Default;
        }
    }

    /// <summary>Writes the settings, returning false when it could not.</summary>
    public static bool Save(TrackerSettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string file = path ?? DefaultPath;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using FileStream stream = File.Create(file);
            JsonSerializer.Serialize(stream, settings, TrackerJsonContext.Default.TrackerSettings);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>Source-generated JSON, so the tracker settings survive Native AOT.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(TrackerSettings))]
public sealed partial class TrackerJsonContext : JsonSerializerContext;
