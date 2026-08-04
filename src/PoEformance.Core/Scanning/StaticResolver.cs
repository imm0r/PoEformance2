using PoEformance.Core.Schema;

namespace PoEformance.Core.Scanning;

/// <summary>Result of resolving one static anchor.</summary>
public sealed record ResolvedStatic(string Name, ulong Address, string Detail)
{
    public bool Found => Address != 0;
}

/// <summary>
/// Turns the schema's pattern-based static anchors into absolute addresses.
/// </summary>
/// <remarks>
/// The pattern convention (inherited from the AHK tool and GameHelper2): hex bytes
/// with <c>??</c> wildcards, and one <c>^</c> marking the start of the RIP-relative
/// disp32. The resolved address is <c>match + caret + 4 + disp32</c>.
///
/// A pattern that matches more than once is reported as ambiguous and NOT resolved -
/// taking "the first hit" of an ambiguous pattern is how tools silently read garbage
/// after a game patch. Better a loud miss than a quiet wrong answer.
/// </remarks>
public sealed class StaticResolver
{
    private readonly PatternScanner _scanner;

    public StaticResolver(PatternScanner scanner)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        _scanner = scanner;
    }

    /// <summary>Resolves every static in the schema. Failures are rows, not exceptions.</summary>
    public List<ResolvedStatic> ResolveAll(OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var results = new List<ResolvedStatic>(schema.Statics.Count);
        foreach (StaticAnchor anchor in schema.Statics.Values)
        {
            results.Add(Resolve(anchor));
        }

        return results;
    }

    /// <summary>Resolves one anchor: scan, disambiguate, follow the RIP displacement.</summary>
    public ResolvedStatic Resolve(StaticAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);

        (BytePattern pattern, int caretOffset) = ParseWithCaret(anchor.Pattern);

        List<ulong> matches = _scanner.FindAll(pattern, maxMatches: 3);
        if (matches.Count == 0)
        {
            return new ResolvedStatic(anchor.Name, 0, "pattern not found");
        }

        if (matches.Count > 1)
        {
            return new ResolvedStatic(anchor.Name, 0, $"ambiguous: {matches.Count}+ matches");
        }

        // RIP-relative: disp32 sits at the caret; the referencing instruction ends
        // right after it (caret + 4) for every pattern we use.
        ulong resolved = _scanner.ResolveRip(matches[0], caretOffset, caretOffset + 4);
        return resolved != 0
            ? new ResolvedStatic(anchor.Name, resolved, $"match at 0x{matches[0]:X}")
            : new ResolvedStatic(anchor.Name, 0, $"match at 0x{matches[0]:X} but RIP target implausible");
    }

    /// <summary>
    /// Splits the '^' marker out of the pattern text. The caret marks a position, not a
    /// byte, so it is removed before parsing and its token index becomes the byte offset.
    /// Public because the RE workbench parses user-entered patterns with the same rules.
    /// </summary>
    public static (BytePattern Pattern, int CaretOffset) ParseWithCaret(string patternWithCaret)
    {
        string[] tokens = patternWithCaret.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int caret = -1;
        var kept = new List<string>(tokens.Length);

        foreach (string token in tokens)
        {
            if (token == "^")
            {
                if (caret >= 0)
                {
                    throw new FormatException($"Pattern has more than one '^': \"{patternWithCaret}\".");
                }

                caret = kept.Count;
            }
            else
            {
                kept.Add(token);
            }
        }

        if (caret < 0)
        {
            throw new FormatException($"Pattern has no '^' RIP marker: \"{patternWithCaret}\".");
        }

        return (BytePattern.Parse(string.Join(' ', kept)), caret);
    }
}
