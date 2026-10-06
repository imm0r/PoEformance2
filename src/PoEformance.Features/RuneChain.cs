using System.Text.Json.Serialization;

namespace PoEformance.Features;

/// <summary>One rune's worth when it propagates down the chain - a row of the settings table.</summary>
/// <param name="Rune">The rune's name as Expedition2Runes spells it: "Opulent".</param>
/// <param name="LootMult">
/// How much more a pack buffed by this rune drops, as a multiplier. 1 is no loot effect (pure
/// danger); below 1 is a net cost, a slot a loot rune could have used.
/// </param>
/// <param name="Avoid">Never worth propagating - flagged red in the table.</param>
public sealed record RuneWeight(
    [property: JsonPropertyName("rune")] string Rune,
    [property: JsonPropertyName("lootMult")] float LootMult,
    [property: JsonPropertyName("avoid")] bool Avoid = false);

/// <summary>Where one monolith stands in the chain: what is already propagating over it.</summary>
/// <param name="TakenMask">
/// A bit per Expedition2Runes row for every rune already in the chain here - committed on some
/// other monolith, or expected from one detonated earlier on the plan. Thirty-four runes fit a
/// ulong, so testing one costs nothing.
/// </param>
/// <param name="PowerUpstream">Whether a monolith earlier on the plan is expected to propagate Power.</param>
public readonly record struct ChainSite(ulong TakenMask, bool PowerUpstream)
{
    /// <summary>A monolith with nothing propagating over it - no plan, nothing committed anywhere.</summary>
    public static ChainSite Alone => default;

    /// <summary>Whether propagating this rune here would be a no-op.</summary>
    public bool Taken(int rune) => rune is >= 0 and < 64 && (TakenMask & (1UL << rune)) != 0;
}

/// <summary>
/// What the rune-chain valuation knows that is not read off the game: the runes, their worth,
/// and the two constants.
/// </summary>
/// <remarks>
/// WHAT THE GOLD SOCKET MEANS (the 0.5.4 notes): a remnant picks which rune SLOT propagates to
/// the monsters unearthed by the explosive placed on it and by every later one, and the panel
/// frames that slot in gold. Buffing those monsters raises their drops - Opulent is "increased
/// monster rarity" outright - so the propagated rune is worth currency, and the socket being a
/// POSITION means the rune every offered recipe would propagate is known before anybody picks:
/// it is the recipe's rune at that hole. That is what lets this RECOMMEND a recipe rather than
/// report one after the fact.
///
/// THE MAGNITUDES ARE SERVER-SIDE and absent from the install, so the multipliers are calibrated,
/// not read: the reference plugin's defaults, which follow the community tier list of 2026-09
/// (SS Opulent; S Power, Death, Bond; A Time, Oath, Rebirth; the rest of the purple runes a
/// little over the blue). Rune rarity is not in the data either - three derivations failed the
/// plugin, the art path contradicting the tome's colours - so the table is the player's own
/// reading, and the settings table lets it be re-tuned.
///
/// POWER MULTIPLIES THE UPLIFT, not the multiplier: a 1.35 rune at the fixed 1.5 becomes
/// 1 + 0.35 x 1.5. The reference fixed the factor and read whether Power is live off the station
/// (the same byte the panel draws the empowered rune art from), and so does this.
/// </remarks>
public static class RuneChain
{
    /// <summary>How much a Power rune multiplies every other rune's uplift down the chain.</summary>
    public const float PowerFactor = 1.5f;

    /// <summary>The rune whose propagation empowers the rest.</summary>
    public const string PowerRune = "Power";

    /// <summary>Expected drop value of one pack of runic monsters, in Exalted - the calibration knob.</summary>
    /// <remarks>
    /// The reference measured 22-58 ex a wave on T15+ Grand expeditions, two samples disagreeing
    /// twofold, and sat at the low end. The whole chain value scales with this.
    /// </remarks>
    public const float DefaultBaseEx = 30f;

    /// <summary>Most runes one map label names when scouting - the rest are on the tab.</summary>
    public const int MostScouted = 3;

    /// <summary>
    /// The thirty-four runes in Expedition2Runes row order - the fallback for the install's own
    /// names, and the list the settings table offers.
    /// </summary>
    public static readonly string[] KnownRunes =
    [
        "Fire", "Cold", "Lightning", "Tempest", "Momentum", "Bloodletting", "Stone", "Adaptive",
        "Arcane", "Toxic", "Electrocuting", "Protective", "Cyclonic", "Vision", "Tidal", "Rebirth",
        "Prismatic", "Gasp", "Moon", "Celestial", "Opulent", "Rage", "Wisdom", "Sky", "Earth", "Life",
        "Bond", "Ward", "Soul", "Death", "Oath", "Time", "Power", "Bait",
    ];

    /// <summary>The tier-list defaults. A rune absent here is worth 1: danger, no loot.</summary>
    public static IReadOnlyList<RuneWeight> DefaultWeights { get; } =
    [
        new("Opulent", 1.35f),
        new("Bond", 1.25f),
        new("Power", 1.30f),
        new("Time", 1.18f),
        new("Death", 1.27f),
        new("Rebirth", 1.10f),
        new("Wisdom", 0.95f, Avoid: true),
        new("Oath", 1.15f),
        new("Bait", 1.00f, Avoid: true),
        new("Life", 1.05f),
        new("Soul", 1.05f),
    ];

    private static readonly Dictionary<string, string> Effects = new(StringComparer.Ordinal)
    {
        ["Fire"] = "monster damage gains as Fire",
        ["Cold"] = "monster damage gains as Cold",
        ["Lightning"] = "monster damage gains as Lightning",
        ["Tempest"] = "hits shock and chill; monsters cannot be shocked",
        ["Momentum"] = "faster, unslowable, slows you",
        ["Bloodletting"] = "life leech and Corrupted Blood on hit",
        ["Stone"] = "armour from strength, huge stun threshold",
        ["Adaptive"] = "adapts resistance to the element that hits it",
        ["Arcane"] = "part of life as energy shield",
        ["Toxic"] = "all damage can poison",
        ["Electrocuting"] = "lightning damage and electrocute",
        ["Protective"] = "special (server-side)",
        ["Cyclonic"] = "special (server-side)",
        ["Vision"] = "reflects curses, shocks, chills",
        ["Tidal"] = "special (server-side)",
        ["Rebirth"] = "revives (server-side)",
        ["Prismatic"] = "resists all elements, random-element damage",
        ["Gasp"] = "volcanic: fire damage and ignite",
        ["Moon"] = "special (server-side)",
        ["Celestial"] = "special (server-side)",
        ["Opulent"] = "increased monster rarity - more loot",
        ["Rage"] = "special (server-side)",
        ["Wisdom"] = "experience from kills - no loot",
        ["Sky"] = "special (server-side)",
        ["Earth"] = "special (server-side)",
        ["Life"] = "tankier, regenerates",
        ["Bond"] = "on death passes a rare mod and heals a rare",
        ["Ward"] = "life as ward, stronger while warded",
        ["Soul"] = "union of souls (shared)",
        ["Death"] = "special (server-side)",
        ["Oath"] = "special (server-side)",
        ["Time"] = "special (server-side)",
        ["Power"] = "empowers every other propagated rune",
        ["Bait"] = "filler - no monster buff",
    };

    /// <summary>What a rune does to the monsters, in a few words, or empty.</summary>
    public static string EffectOf(string rune)
        => rune is { Length: > 0 } && Effects.TryGetValue(rune, out string? effect) ? effect : string.Empty;
}

/// <summary>
/// The weights table compiled against the rune rows: a multiplier per Expedition2Runes index.
/// </summary>
/// <remarks>
/// Built once per settings-and-catalogue pair and kept by reference, so the per-scan work is
/// array lookups. The names come from the install's own table when it has been read and from
/// <see cref="RuneChain.KnownRunes"/> until then - the same order, since the plugin's list is
/// the table's rows.
/// </remarks>
public sealed class RuneChainTable
{
    private readonly float[] _mult;
    private readonly bool[] _avoid;
    private readonly IReadOnlyList<string> _names;

    private RuneChainTable(bool enabled, double baseEx, float[] mult, bool[] avoid, int power, IReadOnlyList<string> names)
    {
        Enabled = enabled;
        BaseEx = baseEx;
        _mult = mult;
        _avoid = avoid;
        PowerIndex = power;
        _names = names;
    }

    /// <summary>The chain switched off: every rune neutral, nothing valued.</summary>
    public static RuneChainTable Off { get; } = new(false, 0, [], [], -1, RuneChain.KnownRunes);

    /// <summary>Whether the chain is valued at all.</summary>
    public bool Enabled { get; }

    /// <summary>Exalted per buffed pack, before any rune.</summary>
    public double BaseEx { get; }

    /// <summary>Power's row, or -1 when no rune of that name is known.</summary>
    public int PowerIndex { get; }

    /// <summary>How many runes the table covers.</summary>
    public int Count => _names.Count;

    /// <summary>Compiles the settings' weights against the catalogue's rune names.</summary>
    public static RuneChainTable Build(RunecraftSettings settings, RecipeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(catalog);

        IReadOnlyList<string> names = catalog.RuneNames.Count > 0 ? catalog.RuneNames : RuneChain.KnownRunes;
        var mult = new float[names.Count];
        var avoid = new bool[names.Count];
        Array.Fill(mult, 1f);
        foreach (RuneWeight weight in settings.ChainWeights)
        {
            int index = IndexOf(names, weight.Rune);
            if (index >= 0)
            {
                mult[index] = float.IsFinite(weight.LootMult) ? Math.Clamp(weight.LootMult, 0f, 10f) : 1f;
                avoid[index] = weight.Avoid;
            }
        }

        return new RuneChainTable(
            settings.ChainEnabled, Math.Max(0f, settings.ChainBaseEx), mult, avoid, IndexOf(names, RuneChain.PowerRune), names);
    }

    /// <summary>A rune's name by row, or "#n".</summary>
    public string Name(int rune)
        => rune >= 0 && rune < _names.Count && _names[rune].Length > 0 ? _names[rune] : $"#{rune}";

    /// <summary>The table's multiplier for a rune: 1 for one it does not list.</summary>
    public double LootMult(int rune) => rune >= 0 && rune < _mult.Length ? _mult[rune] : 1.0;

    /// <summary>Whether the table says never to propagate this rune.</summary>
    public bool Avoid(int rune) => rune >= 0 && rune < _avoid.Length && _avoid[rune];

    /// <summary>The rune's loot multiplier with Power's empowerment applied to the uplift.</summary>
    public double EffMult(int rune, bool empowered)
        => 1.0 + ((LootMult(rune) - 1.0) * (empowered ? RuneChain.PowerFactor : 1.0));

    /// <summary>
    /// The multiplier AT a monolith: neutral where the rune is already in the chain, empowered
    /// where Power is expected upstream.
    /// </summary>
    public double EffMultAt(in ChainSite site, int rune, bool empowered)
        => site.Taken(rune) ? 1.0 : EffMult(rune, empowered || site.PowerUpstream);

    /// <summary>Exalted a propagated rune adds to one buffed wave.</summary>
    public double ExPerWave(int rune, bool empowered) => BaseEx * (EffMult(rune, empowered) - 1.0);

    /// <summary>
    /// The rune a recipe would drop on the gold socket - the best-valued at this site when the
    /// station frames several - or -1 when none of its holes is framed.
    /// </summary>
    /// <remarks>
    /// The station's vector can hold more than one position (officially one; a second element
    /// is unverified in the reference), and the best of them is taken rather than every one
    /// marked, so no new assumption about the second rides in.
    /// </remarks>
    public int Propagated(in ChainSite site, IReadOnlyList<int> glowSockets, IReadOnlyList<int> runes, bool empowered)
    {
        ArgumentNullException.ThrowIfNull(glowSockets);
        ArgumentNullException.ThrowIfNull(runes);

        int best = -1;
        double bestMult = double.NegativeInfinity;
        foreach (int socket in glowSockets)
        {
            if (socket < 0 || socket >= runes.Count)
            {
                continue;
            }

            int rune = runes[socket];
            if (rune < 0)
            {
                continue;
            }

            double mult = EffMultAt(site, rune, empowered);
            if (mult > bestMult)
            {
                bestMult = mult;
                best = rune;
            }
        }

        return best;
    }

    private static int IndexOf(IReadOnlyList<string> names, string rune)
    {
        if (rune.Length == 0)
        {
            return -1;
        }

        for (int i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], rune, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>What one monolith contributes to the area's chain plan.</summary>
/// <param name="Id">The device entity's id.</param>
/// <param name="Foreign">The standalone monolith, outside the chain altogether.</param>
/// <param name="Waves">Packs it is expected to raise: its committed or recommended recipe's length, else its holes.</param>
/// <param name="LockedRune">The rune its committed recipe propagates, or -1 while open.</param>
/// <param name="ExpectedRune">The rune it is expected to propagate: the locked one, else the last recommendation's, else -1.</param>
public readonly record struct ChainStand(uint Id, bool Foreign, int Waves, int LockedRune, int ExpectedRune);

/// <summary>
/// The area's chain as a whole: what is committed anywhere, and, along a planned detonation
/// order, what propagates over each monolith and how many waves are still ahead of it.
/// </summary>
/// <remarks>
/// WHY A PLAN AT ALL. The chain value of a rune is the packs it still buffs, and those are the
/// monolith's own waves plus every monolith detonated AFTER it. The reference first counted one
/// wave per charge left and measured that as a two-to-four-fold under-estimate in its simulator:
/// most charges only extend the chain, while one eight-hole monolith is eight waves. So the
/// count comes from the monoliths themselves, in the planner's order, each at the waves it is
/// expected to spawn - and without an order (no planner yet, or a monolith off its route) from
/// every other live monolith's waves, which is an upper bound and still far closer.
///
/// THREE PASSES. What is committed (pass 0) is a fact everywhere: two monoliths cannot both
/// propagate Opulent, "modifiers of the same type no longer stack", and the sealed one is the one
/// that cannot be changed. Forwards (pass 1) along the order: Power already propagating empowers
/// everything after it, and the set of runes already propagating makes a repeat worthless - which
/// changes the RECOMMENDATION, not just the figure, since a downstream monolith offering Opulent
/// or Bond should be told Bond once Opulent is in. Backwards (pass 2): the waves still ahead, and
/// the Exalted of uplift still ahead that a Power here would multiply - Power's whole point, and
/// the reason it is not just a 1.30 rune.
///
/// Each monolith's expectation comes from the previous scan's recommendation, so a fresh area
/// converges over a few scans (position one at once, two on the next); values only sharpen.
/// </remarks>
public sealed class RuneChainPlan
{
    private readonly struct Entry(ulong own, ulong active, bool powerUp, int wavesAhead, double upliftAhead, bool onPlan)
    {
        public readonly ulong Own = own;
        public readonly ulong Active = active;
        public readonly bool PowerUp = powerUp;
        public readonly int WavesAhead = wavesAhead;
        public readonly double UpliftAhead = upliftAhead;
        public readonly bool OnPlan = onPlan;
    }

    private readonly Dictionary<uint, Entry> _entries;
    private readonly ulong _lockedMask;

    private RuneChainPlan(Dictionary<uint, Entry> entries, ulong lockedMask, int wavesTotal, int ordered)
    {
        _entries = entries;
        _lockedMask = lockedMask;
        WavesTotal = wavesTotal;
        Ordered = ordered;
    }

    /// <summary>No monoliths at all.</summary>
    public static RuneChainPlan Empty { get; } = new([], 0, 0, 0);

    /// <summary>The waves of every live monolith added up - the no-order fallback.</summary>
    public int WavesTotal { get; }

    /// <summary>How many monoliths the order placed.</summary>
    public int Ordered { get; }

    /// <summary>Builds the plan from every monolith's stand and the planned order of their ids.</summary>
    /// <param name="table">The weights, for what Power is and what a wave is worth.</param>
    /// <param name="stands">Every monolith in the area.</param>
    /// <param name="order">Device ids in detonation order, or empty for no plan. Ids not in <paramref name="stands"/> are skipped.</param>
    public static RuneChainPlan Build(RuneChainTable table, IReadOnlyList<ChainStand> stands, IReadOnlyList<uint> order)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(stands);
        ArgumentNullException.ThrowIfNull(order);

        var entries = new Dictionary<uint, Entry>(stands.Count);
        ulong lockedMask = 0;
        var wavesTotal = 0;

        // Pass 0: what is already committed, on the plan or not.
        var byId = new Dictionary<uint, ChainStand>(stands.Count);
        foreach (ChainStand stand in stands)
        {
            if (stand.Foreign)
            {
                continue;
            }

            byId.TryAdd(stand.Id, stand);
            wavesTotal += Math.Max(1, stand.Waves);
            ulong own = Bit(stand.LockedRune);
            lockedMask |= own;
            entries[stand.Id] = new Entry(own, 0, false, 0, 0, false);
        }

        // The order, as stands, once each.
        var ordered = new List<ChainStand>(order.Count);
        foreach (uint id in order)
        {
            if (byId.TryGetValue(id, out ChainStand stand) && !ordered.Contains(stand))
            {
                ordered.Add(stand);
            }
        }

        if (ordered.Count == 0)
        {
            return new RuneChainPlan(entries, lockedMask, wavesTotal, 0);
        }

        // Pass 1, forwards: what propagates by the time each one is detonated.
        var powerUp = new bool[ordered.Count];
        var active = new ulong[ordered.Count];
        var power = false;
        ulong propagating = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            powerUp[i] = power;
            active[i] = propagating;
            int expected = ordered[i].ExpectedRune;
            if (expected < 0)
            {
                continue;
            }

            if (expected == table.PowerIndex)
            {
                power = true;
            }

            propagating |= Bit(expected);
        }

        // Pass 2, backwards: the waves still ahead, and the uplift a Power here would multiply.
        // The rune expected at each is valued unempowered - Power's factor is what multiplies it,
        // so folding an empowerment in would count it twice - and a duplicate adds nothing, so
        // Power gets no credit for "empowering" it either.
        var acc = 0;
        double upliftAcc = 0;
        for (int i = ordered.Count - 1; i >= 0; i--)
        {
            ChainStand stand = ordered[i];
            ulong own = Bit(stand.LockedRune);
            entries[stand.Id] = new Entry(own, active[i], powerUp[i], acc, upliftAcc, true);
            acc += Math.Max(1, stand.Waves);

            int expected = stand.ExpectedRune;
            if (expected >= 0 && expected != table.PowerIndex
                && !Duplicate(lockedMask, own, active[i], expected))
            {
                upliftAcc += table.ExPerWave(expected, false) * acc;
            }
        }

        return new RuneChainPlan(entries, lockedMask, wavesTotal, ordered.Count);
    }

    /// <summary>Where a monolith stands: what is taken over it and whether Power is upstream.</summary>
    public ChainSite SiteOf(uint id)
    {
        if (!_entries.TryGetValue(id, out Entry entry))
        {
            return new ChainSite(_lockedMask, false);
        }

        return new ChainSite((_lockedMask & ~entry.Own) | entry.Active, entry.PowerUp);
    }

    /// <summary>Whether a monolith was placed by the order.</summary>
    public bool OnPlan(uint id) => _entries.TryGetValue(id, out Entry entry) && entry.OnPlan;

    /// <summary>
    /// Packs a rune propagated by a recipe of this length here would still buff: its own waves
    /// and every monolith still ahead - or every other live monolith when there is no order.
    /// </summary>
    public double DownstreamPacks(uint id, int recipeSize)
    {
        int own = Math.Max(1, recipeSize);
        if (_entries.TryGetValue(id, out Entry entry) && entry.OnPlan)
        {
            return own + entry.WavesAhead;
        }

        return own + Math.Max(0, WavesTotal - own);
    }

    /// <summary>The Exalted of uplift still ahead of a monolith that a Power there would multiply.</summary>
    public double UpliftAhead(uint id) => _entries.TryGetValue(id, out Entry entry) ? entry.UpliftAhead : 0;

    /// <summary>
    /// What propagating a rune from a recipe of this length here is worth, in Exalted. Zero for
    /// a rune already in the chain; negative for one whose multiplier is below 1.
    /// </summary>
    public double ChainEx(RuneChainTable table, uint id, int rune, int recipeSize, bool empowered)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (rune < 0)
        {
            return 0;
        }

        ChainSite site = SiteOf(id);
        if (site.Taken(rune))
        {
            return 0;
        }

        bool live = empowered || site.PowerUpstream;
        double ex = table.ExPerWave(rune, live) * DownstreamPacks(id, recipeSize);

        // Propagating Power also multiplies every rune after it - measured on a nineteen-monolith
        // plan, Power's own share was 540 ex against a true worth of 880.
        if (rune == table.PowerIndex && !live)
        {
            ex += (RuneChain.PowerFactor - 1.0) * UpliftAhead(id);
        }

        return ex;
    }

    private static bool Duplicate(ulong lockedMask, ulong own, ulong active, int rune)
    {
        ulong bit = Bit(rune);
        return bit != 0 && (((lockedMask & ~own) | active) & bit) != 0;
    }

    private static ulong Bit(int rune) => rune is >= 0 and < 64 ? 1UL << rune : 0;
}
