using PoEformance.Core.Scanning;

namespace PoEformance.Core.Tests;

public class PatternTests
{
    [Fact]
    public void Parse_RejectsGarbageWithTheOffendingToken()
    {
        var ex = Assert.Throws<FormatException>(() => BytePattern.Parse("48 8B ZZ"));
        Assert.Contains("ZZ", ex.Message);
        Assert.Throws<FormatException>(() => BytePattern.Parse("?? 48"));
        Assert.Throws<ArgumentException>(() => BytePattern.Parse("   ")); // whitespace = argument error, not format
    }

    [Fact]
    public void FindAll_MatchesWithWildcards()
    {
        byte[] haystack = [0x00, 0x48, 0x8B, 0x05, 0x99, 0x48, 0x8B, 0xFF, 0x11];
        var pattern = BytePattern.Parse("48 8B ??");

        List<int> matches = pattern.FindAll(haystack);
        Assert.Equal([1, 5], matches);
    }

    [Fact]
    public void FindAll_HonoursMaxMatches()
    {
        byte[] haystack = [0xAA, 0xAA, 0xAA, 0xAA];
        var pattern = BytePattern.Parse("AA");
        Assert.Single(pattern.FindAll(haystack, maxMatches: 1));
    }

    [Fact]
    public void CaretParsing_SplitsMarkerFromBytes()
    {
        (BytePattern pattern, int caret) = StaticResolver.ParseWithCaret("48 39 2D ^ ?? ?? ?? ?? 0F 85");
        Assert.Equal(3, caret);
        Assert.Equal(9, pattern.Length); // caret is a position, not a byte

        Assert.Throws<FormatException>(() => StaticResolver.ParseWithCaret("48 39 2D"));
        Assert.Throws<FormatException>(() => StaticResolver.ParseWithCaret("48 ^ 39 ^ 2D ?? ?? ?? ??"));
    }

    [Fact]
    public void Resolve_FollowsRipDisplacement()
    {
        // Build a fake module: cmp [rip+disp32], rbp at module+0x100 pointing to module+0x2000.
        const ulong moduleBase = 0x1400000000;
        var module = new byte[0x3000];
        module[0x100] = 0x48;
        module[0x101] = 0x39;
        module[0x102] = 0x2D;
        // disp32 = target - (instr + 7) = 0x2000 - 0x107
        int disp = 0x2000 - 0x107;
        BitConverter.GetBytes(disp).CopyTo(module, 0x103);
        module[0x107] = 0x0F;
        module[0x108] = 0x85;

        var reader = new FakeMemoryReader { ModuleBase = moduleBase, ModuleSize = (uint)module.Length };
        reader.Place(moduleBase, module);

        var resolver = new StaticResolver(new PatternScanner(reader));
        ResolvedStatic result = resolver.Resolve(new Core.Schema.StaticAnchor(
            "GameStates", "48 39 2D ^ ?? ?? ?? ?? 0F 85", null));

        Assert.True(result.Found);
        Assert.Equal(moduleBase + 0x2000, result.Address);
    }

    /// <summary>
    /// A module in which the primary GameStates pattern no longer exists because the compiler
    /// picked another register: two `cmp [rip+X], reg` sites survive, one referencing the real
    /// static and one a decoy.
    /// </summary>
    private static (FakeMemoryReader Reader, ulong Real, ulong Decoy) BuildRerolledModule()
    {
        const ulong moduleBase = 0x1400000000;
        var module = new byte[0x4000];

        static void PlaceCmpSite(byte[] module, int at, byte modrm, int target, byte[] tail)
        {
            module[at] = 0x48;
            module[at + 1] = 0x39;
            module[at + 2] = modrm;
            BitConverter.GetBytes(target - (at + 7)).CopyTo(module, at + 3);
            tail.CopyTo(module, at + 7);
        }

        byte[] jnzThenAlloc = [0x0F, 0x85, 0x10, 0x20, 0x30, 0x40, 0xB9, 0x40, 0x01, 0x00, 0x00];
        PlaceCmpSite(module, 0x100, 0x35, 0x2000, jnzThenAlloc); // rsi now, was rbp - the real site
        PlaceCmpSite(module, 0x900, 0x3D, 0x2800, jnzThenAlloc); // rdi - some other global, same shape

        var reader = new FakeMemoryReader { ModuleBase = moduleBase, ModuleSize = (uint)module.Length };
        reader.Place(moduleBase, module);
        return (reader, moduleBase + 0x2000, moduleBase + 0x2800);
    }

    private static Core.Schema.StaticAnchor RerolledAnchor() => new(
        "GameStates",
        "48 39 2D ^ ?? ?? ?? ?? 0F 85 ?? ?? ?? ?? B9 40 01 00 00",
        null,
        ["48 39 ?? ^ ?? ?? ?? ?? 0F 85 ?? ?? ?? ?? B9 40 01 00 00"]);

    [Fact]
    public void Fallback_ResolvesTheHitTheFingerprintAccepts_AndShowsItsBytes()
    {
        (FakeMemoryReader reader, ulong real, ulong decoy) = BuildRerolledModule();
        var resolver = new StaticResolver(new PatternScanner(reader));

        ResolvedStatic result = resolver.Resolve(RerolledAnchor(), fingerprint: address => address == real);

        Assert.True(result.Found);
        Assert.True(result.ViaFallback);
        Assert.Equal(real, result.Address);

        // Both sites are reported, the decoy marked as such, and the accepted site's bytes
        // begin with the register byte that changed - the new primary pattern, verbatim.
        Assert.NotNull(result.Candidates);
        Assert.Equal(2, result.Candidates.Count);
        StaticCandidate accepted = Assert.Single(result.Candidates, c => c.Fingerprinted);
        Assert.Equal(real, accepted.Resolved);
        Assert.StartsWith("48 39 35 ", accepted.SiteText(), StringComparison.Ordinal);
        Assert.EndsWith("0F 85 10 20 30 40 B9 40 01 00 00", accepted.SiteText(), StringComparison.Ordinal);
        Assert.Contains(result.Candidates, c => !c.Fingerprinted && c.Resolved == decoy);
    }

    [Fact]
    public void Fallback_WithoutAFingerprint_DoesNotPickBetweenTwoHits()
    {
        // The loose pattern hits twice and nothing can tell them apart: that is the same
        // ambiguity the primary refuses to guess at, and the fallback refuses too - but it
        // still hands over both sites so a person can.
        (FakeMemoryReader reader, _, _) = BuildRerolledModule();
        var resolver = new StaticResolver(new PatternScanner(reader));

        ResolvedStatic result = resolver.Resolve(RerolledAnchor());

        Assert.False(result.Found);
        Assert.Contains("pattern not found", result.Detail);
        Assert.NotNull(result.Candidates);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public void Fallback_RejectedByTheFingerprintEverywhere_StaysAMiss()
    {
        (FakeMemoryReader reader, _, _) = BuildRerolledModule();
        var resolver = new StaticResolver(new PatternScanner(reader));

        ResolvedStatic result = resolver.Resolve(RerolledAnchor(), fingerprint: _ => false);

        Assert.False(result.Found);
        Assert.Contains("nothing passed the fingerprint", result.Detail);
        Assert.All(result.Candidates!, c => Assert.False(c.Fingerprinted));
    }

    [Fact]
    public void Fallback_TwoSitesReferencingTheSameStatic_AreOneAnswer()
    {
        // A global read from two functions: a loose pattern finds both sites, both resolve
        // to the same address, and that is not an ambiguity.
        const ulong moduleBase = 0x1400000000;
        var module = new byte[0x4000];
        foreach (int at in new[] { 0x100, 0x900 })
        {
            module[at] = 0x48;
            module[at + 1] = 0x39;
            module[at + 2] = 0x35;
            BitConverter.GetBytes(0x2000 - (at + 7)).CopyTo(module, at + 3);
            module[at + 7] = 0x0F;
            module[at + 8] = 0x85;
        }

        var reader = new FakeMemoryReader { ModuleBase = moduleBase, ModuleSize = (uint)module.Length };
        reader.Place(moduleBase, module);
        var resolver = new StaticResolver(new PatternScanner(reader));

        ResolvedStatic result = resolver.Resolve(
            new Core.Schema.StaticAnchor("X", "48 39 2D ^ ?? ?? ?? ?? 0F 85", null, ["48 39 ?? ^ ?? ?? ?? ?? 0F 85"]),
            fingerprint: address => address == moduleBase + 0x2000);

        Assert.True(result.Found);
        Assert.Equal(moduleBase + 0x2000, result.Address);
        Assert.Equal(2, result.Candidates!.Count);
    }

    [Fact]
    public void PrimaryHit_StandsOnItsOwn_ButSaysWhenTheFingerprintDisagrees()
    {
        const ulong moduleBase = 0x1400000000;
        var module = new byte[0x3000];
        module[0x100] = 0x48;
        module[0x101] = 0x39;
        module[0x102] = 0x2D;
        BitConverter.GetBytes(0x2000 - 0x107).CopyTo(module, 0x103);
        module[0x107] = 0x0F;
        module[0x108] = 0x85;
        var reader = new FakeMemoryReader { ModuleBase = moduleBase, ModuleSize = (uint)module.Length };
        reader.Place(moduleBase, module);
        var resolver = new StaticResolver(new PatternScanner(reader));
        var anchor = new Core.Schema.StaticAnchor("GameStates", "48 39 2D ^ ?? ?? ?? ?? 0F 85", null);

        ResolvedStatic trusted = resolver.Resolve(anchor, fingerprint: _ => true);
        ResolvedStatic doubted = resolver.Resolve(anchor, fingerprint: _ => false);

        // The AreaChangeCounter lesson: a unique hit is still resolved (nothing better exists),
        // but the row says the fingerprint disagrees rather than looking like a clean success.
        Assert.True(trusted.Found);
        Assert.DoesNotContain("REJECTS", trusted.Detail);
        Assert.True(doubted.Found);
        Assert.Contains("REJECTS", doubted.Detail);
        Assert.False(doubted.ViaFallback);
    }

    [Fact]
    public void Resolve_ReportsAmbiguousPatternsInsteadOfGuessing()
    {
        const ulong moduleBase = 0x1400000000;
        var module = new byte[0x1000];
        // Two identical instruction sites - a pattern that matches both must NOT resolve.
        foreach (int at in new[] { 0x100, 0x500 })
        {
            module[at] = 0x48;
            module[at + 1] = 0x39;
            module[at + 2] = 0x2D;
        }

        var reader = new FakeMemoryReader { ModuleBase = moduleBase, ModuleSize = (uint)module.Length };
        reader.Place(moduleBase, module);

        var resolver = new StaticResolver(new PatternScanner(reader));
        ResolvedStatic result = resolver.Resolve(new Core.Schema.StaticAnchor(
            "Ambiguous", "48 39 2D ^ ?? ?? ?? ??", null));

        Assert.False(result.Found);
        Assert.Contains("ambiguous", result.Detail);
    }
}
