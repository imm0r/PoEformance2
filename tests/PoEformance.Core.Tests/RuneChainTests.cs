using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>The rune-chain model: the weights compiled, the plan's three passes, and what a rune is worth where.</summary>
public class RuneChainTests
{
    private const int Tempest = 3;
    private const int Opulent = 20;
    private const int Bond = 26;
    private const int Death = 29;
    private const int Power = 32;
    private const int Wisdom = 22;

    private static RuneChainTable Table(float baseEx = 30f, bool enabled = true, IReadOnlyList<RuneWeight>? weights = null)
        => RuneChainTable.Build(
            new RunecraftSettings(ChainEnabled: enabled, ChainBaseEx: baseEx, ChainWeights: weights),
            RecipeCatalogTests.With([]));

    [Fact]
    public void TheDefaultsAreTheTierList_AndTheKnownRunesAreTheTablesRows()
    {
        Assert.Equal(34, RuneChain.KnownRunes.Length);
        Assert.Equal("Opulent", RuneChain.KnownRunes[Opulent]);
        Assert.Equal("Power", RuneChain.KnownRunes[Power]);

        RuneWeight opulent = Assert.Single(RuneChain.DefaultWeights, w => w.Rune == "Opulent");
        Assert.Equal(1.35f, opulent.LootMult);
        Assert.False(opulent.Avoid);
        Assert.True(Assert.Single(RuneChain.DefaultWeights, w => w.Rune == "Wisdom").Avoid);
        Assert.Equal(30f, RuneChain.DefaultBaseEx);
        Assert.Contains("loot", RuneChain.EffectOf("Opulent"), StringComparison.Ordinal);
        Assert.Equal(string.Empty, RuneChain.EffectOf("NotARune"));
    }

    [Fact]
    public void TheTableCompilesTheWeightsAgainstTheRuneRows_AndFallsBackToTheKnownNames()
    {
        RuneChainTable table = Table();
        Assert.True(table.Enabled);
        Assert.Equal(30.0, table.BaseEx);
        Assert.Equal(34, table.Count);
        Assert.Equal(Power, table.PowerIndex);
        Assert.Equal("Opulent", table.Name(Opulent));
        Assert.Equal(1.35, table.LootMult(Opulent), 4);
        Assert.Equal(1.0, table.LootMult(Tempest), 4);
        Assert.True(table.Avoid(Wisdom));
        Assert.False(table.Avoid(Opulent));

        // No catalogue at all: the known thirty-four stand in, same order.
        RuneChainTable fallback = RuneChainTable.Build(new RunecraftSettings(), RecipeCatalog.Empty);
        Assert.Equal(34, fallback.Count);
        Assert.Equal("Bond", fallback.Name(Bond));
        Assert.Equal(1.25, fallback.LootMult(Bond), 4);

        // A rune the table does not know is ignored; a wild multiplier is clamped; off is off.
        RuneChainTable odd = Table(weights: [new("Dragon", 9f), new("Opulent", 99f), new("Bond", float.NaN)]);
        Assert.Equal(10.0, odd.LootMult(Opulent), 4);
        Assert.Equal(1.0, odd.LootMult(Bond), 4);
        Assert.False(Table(enabled: false).Enabled);
        Assert.Equal("#40", table.Name(40));
    }

    [Fact]
    public void PowerMultipliesTheUplift_NotTheMultiplier()
    {
        RuneChainTable table = Table();
        Assert.Equal(1.35, table.EffMult(Opulent, empowered: false), 4);
        Assert.Equal(1.525, table.EffMult(Opulent, empowered: true), 4);
        Assert.Equal(10.5, table.ExPerWave(Opulent, false), 4);
        Assert.Equal(15.75, table.ExPerWave(Opulent, true), 4);
        Assert.Equal(-1.5, table.ExPerWave(Wisdom, false), 4);
        Assert.Equal(0.0, table.ExPerWave(Tempest, true), 4);

        // At a site: taken reads neutral however strong, Power upstream empowers.
        var taken = new ChainSite(1UL << Opulent, PowerUpstream: false);
        Assert.Equal(1.0, table.EffMultAt(taken, Opulent, false), 4);
        Assert.Equal(1.25, table.EffMultAt(taken, Bond, false), 4);
        var powered = new ChainSite(0, PowerUpstream: true);
        Assert.Equal(1.525, table.EffMultAt(powered, Opulent, false), 4);
        Assert.True(taken.Taken(Opulent));
        Assert.False(taken.Taken(Bond));
        Assert.False(ChainSite.Alone.Taken(Opulent));
    }

    [Fact]
    public void ThePropagatedRuneIsTheBestOfTheFramedSockets()
    {
        RuneChainTable table = Table();
        int[] runes = [7, Bond, Opulent, 0, Tempest];

        Assert.Equal(Opulent, table.Propagated(ChainSite.Alone, [2], runes, false));
        Assert.Equal(Opulent, table.Propagated(ChainSite.Alone, [1, 2], runes, false));
        Assert.Equal(Bond, table.Propagated(ChainSite.Alone, [1, 4], runes, false));
        Assert.Equal(Tempest, table.Propagated(ChainSite.Alone, [4], runes, false));

        // A socket past the recipe's length frames nothing; so does an empty vector.
        Assert.Equal(-1, table.Propagated(ChainSite.Alone, [5], runes, false));
        Assert.Equal(-1, table.Propagated(ChainSite.Alone, [], runes, false));

        // At a site where Opulent is already in, Bond is the better of the two.
        Assert.Equal(Bond, table.Propagated(new ChainSite(1UL << Opulent, false), [1, 2], runes, false));
    }

    [Fact]
    public void WithoutAnOrderThePacksAheadAreEveryOtherMonolithsWaves()
    {
        RuneChainTable table = Table();
        ChainStand[] stands =
        [
            new(1, Foreign: false, Waves: 5, LockedRune: -1, ExpectedRune: -1),
            new(2, Foreign: false, Waves: 4, LockedRune: -1, ExpectedRune: -1),
            new(3, Foreign: true, Waves: 8, LockedRune: -1, ExpectedRune: -1),
        ];
        RuneChainPlan plan = RuneChainPlan.Build(table, stands, []);

        Assert.Equal(9, plan.WavesTotal);
        Assert.Equal(0, plan.Ordered);
        Assert.False(plan.OnPlan(1));
        Assert.Equal(9.0, plan.DownstreamPacks(1, 5), 4);
        Assert.Equal(9.0, plan.DownstreamPacks(2, 2), 4);
        Assert.Equal(ChainSite.Alone, plan.SiteOf(1));
        Assert.Equal(10.5 * 9, plan.ChainEx(table, 1, Opulent, 5, empowered: false), 4);
        Assert.Equal(15.75 * 9, plan.ChainEx(table, 1, Opulent, 5, empowered: true), 4);
        Assert.Equal(0.0, plan.ChainEx(table, 1, -1, 5, false), 4);
        Assert.Equal(0.0, plan.ChainEx(table, 1, Tempest, 5, false), 4);
        Assert.True(plan.ChainEx(table, 1, Wisdom, 5, false) < 0);
    }

    [Fact]
    public void ARuneCommittedAnywhereIsDeadEverywhere_ExceptOnItsOwnMonolith()
    {
        RuneChainTable table = Table();
        ChainStand[] stands =
        [
            new(1, false, 5, LockedRune: Opulent, ExpectedRune: Opulent),
            new(2, false, 4, -1, -1),
        ];
        RuneChainPlan plan = RuneChainPlan.Build(table, stands, []);

        Assert.True(plan.SiteOf(2).Taken(Opulent));
        Assert.False(plan.SiteOf(1).Taken(Opulent));
        Assert.Equal(0.0, plan.ChainEx(table, 2, Opulent, 4, false), 4);
        Assert.True(plan.ChainEx(table, 1, Opulent, 5, false) > 0);

        // A monolith the plan never heard of still sees what is locked.
        Assert.True(plan.SiteOf(99).Taken(Opulent));
        Assert.Equal(RuneChainPlan.Empty.SiteOf(1), ChainSite.Alone);
    }

    [Fact]
    public void AlongTheOrderPowerEmpowersWhatFollows_AndARuneAlreadyInIsDeadDownstream()
    {
        RuneChainTable table = Table();
        ChainStand[] stands =
        [
            new(10, false, Waves: 3, -1, ExpectedRune: Power),
            new(20, false, Waves: 4, -1, ExpectedRune: Opulent),
            new(30, false, Waves: 5, -1, ExpectedRune: Bond),
            new(40, false, Waves: 6, -1, -1),
        ];

        // The order names 20 twice and one id that is no monolith; both are harmless.
        RuneChainPlan plan = RuneChainPlan.Build(table, stands, [10, 20, 20, 30, 77]);
        Assert.Equal(3, plan.Ordered);
        Assert.True(plan.OnPlan(10));
        Assert.False(plan.OnPlan(40));

        // Forwards: Power upstream of 20 and 30, Opulent in by 30.
        Assert.False(plan.SiteOf(10).PowerUpstream);
        Assert.True(plan.SiteOf(20).PowerUpstream);
        Assert.True(plan.SiteOf(30).PowerUpstream);
        Assert.False(plan.SiteOf(20).Taken(Opulent));
        Assert.True(plan.SiteOf(30).Taken(Opulent));
        Assert.False(plan.SiteOf(30).Taken(Bond));

        // Backwards: the waves still ahead, own included in the packs a rune buffs.
        Assert.Equal(3 + 9, plan.DownstreamPacks(10, 3), 4);
        Assert.Equal(4 + 5, plan.DownstreamPacks(20, 4), 4);
        Assert.Equal(5 + 0, plan.DownstreamPacks(30, 5), 4);

        // Off the plan, 40 falls back to everyone else's waves.
        Assert.Equal(6 + 12, plan.DownstreamPacks(40, 6), 4);

        // The uplift ahead of 10 is what Bond and Opulent, unempowered, add over the waves
        // from their monolith onwards: Bond 7.5 x 5, Opulent 10.5 x 9.
        Assert.Equal(0.0, plan.UpliftAhead(30), 4);
        Assert.Equal(37.5, plan.UpliftAhead(20), 4);
        Assert.Equal(132.0, plan.UpliftAhead(10), 4);

        // Power at 10: its own 9 ex a wave over 12 packs, plus half of the 132 it empowers.
        Assert.Equal(108.0 + 66.0, plan.ChainEx(table, 10, Power, 3, false), 4);

        // Opulent at 20, empowered by the Power upstream, over its own 4 and the 5 after.
        Assert.Equal(15.75 * 9, plan.ChainEx(table, 20, Opulent, 4, false), 4);

        // At 30 Opulent is dead, Bond is empowered over its own 5.
        Assert.Equal(0.0, plan.ChainEx(table, 30, Opulent, 5, false), 4);
        Assert.Equal(11.25 * 5, plan.ChainEx(table, 30, Bond, 5, false), 4);

        // Power where Power is already upstream gets no empowerment credit, and no factor.
        Assert.Equal(0.0, plan.ChainEx(table, 20, Power, 4, false), 4);
    }

    [Fact]
    public void AnExpectedDuplicateAddsNoUplift_AndALockedRuneCountsBeforeTheOrder()
    {
        RuneChainTable table = Table();
        ChainStand[] stands =
        [
            new(1, false, 3, -1, ExpectedRune: Opulent),
            new(2, false, 3, -1, ExpectedRune: Opulent),
            new(3, false, 3, LockedRune: Death, ExpectedRune: Death),
        ];
        RuneChainPlan plan = RuneChainPlan.Build(table, stands, [1, 2]);

        // 2's Opulent is a duplicate of 1's, so 1 is credited nothing for it ahead.
        Assert.Equal(0.0, plan.UpliftAhead(1), 4);
        Assert.True(plan.SiteOf(2).Taken(Opulent));

        // 3 is off the order and sealed: Death is dead at 1 and 2, alive on 3 itself.
        Assert.True(plan.SiteOf(1).Taken(Death));
        Assert.False(plan.SiteOf(3).Taken(Death));
        Assert.Equal(0.0, plan.ChainEx(table, 1, Death, 3, false), 4);
    }
}
