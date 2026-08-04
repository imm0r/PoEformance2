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
