using System.Text;
using PoEformance.Core.Schema;

namespace PoEformance.Core.Scanning;

/// <summary>One hit of a fallback pattern, with everything needed to judge it by eye.</summary>
/// <param name="FallbackIndex">1-based index of the fallback pattern that produced it.</param>
/// <param name="MatchAddress">Where the pattern matched in the module.</param>
/// <param name="Resolved">The static address the RIP displacement at the hit points to.</param>
/// <param name="SiteBytes">The bytes at the match site, as long as the pattern - the exact pattern of this site.</param>
/// <param name="Fingerprinted">True when the caller's fingerprint check accepted <paramref name="Resolved"/>.</param>
public sealed record StaticCandidate(int FallbackIndex, ulong MatchAddress, ulong Resolved, byte[] SiteBytes, bool Fingerprinted)
{
    /// <summary>The site bytes as pattern text, ready to paste into the schema.</summary>
    public string SiteText()
    {
        var text = new StringBuilder(SiteBytes.Length * 3);
        foreach (byte b in SiteBytes)
        {
            text.Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)).Append(' ');
        }

        return text.ToString().TrimEnd();
    }
}

/// <summary>Result of resolving one static anchor.</summary>
/// <param name="Candidates">
/// Every fallback hit that was examined, when the primary pattern did not settle it. Empty
/// when the primary resolved or the anchor has no fallbacks.
/// </param>
public sealed record ResolvedStatic(string Name, ulong Address, string Detail, IReadOnlyList<StaticCandidate>? Candidates = null)
{
    public bool Found => Address != 0;

    /// <summary>True when the address came from a fallback pattern rather than the primary.</summary>
    /// <remarks>
    /// Worth surfacing separately: a fallback resolution keeps the session working, but it
    /// is a loosened match and the schema's primary pattern is stale. The report prints the
    /// site bytes so the primary can be re-anchored on them.
    /// </remarks>
    public bool ViaFallback => Found && Candidates is { Count: > 0 };
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
///
/// When the primary pattern misses, the anchor's fallbacks are tried - see
/// <see cref="StaticAnchor.Fallbacks"/>. Those are loose on purpose and hit other sites
/// too, so a fallback hit only counts when the caller's fingerprint check accepts what it
/// resolves to, and only when exactly one distinct address passes. The same rule as above,
/// applied to a set that is expected to contain decoys.
/// </remarks>
public sealed class StaticResolver
{
    /// <summary>
    /// How many hits of one fallback are worth examining. A loose pattern that hits more than
    /// this is not narrowing anything down, and the report would be a wall of decoys.
    /// </summary>
    private const int MaxFallbackHits = 24;

    private readonly PatternScanner _scanner;

    public StaticResolver(PatternScanner scanner)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        _scanner = scanner;
    }

    /// <summary>Resolves every static in the schema. Failures are rows, not exceptions.</summary>
    /// <param name="fingerprints">
    /// Per-static checks of what a resolved address should lead to, keyed by static name.
    /// Only consulted for fallback candidates; a static without one can be resolved by a
    /// fallback only when that fallback hits exactly once.
    /// </param>
    public List<ResolvedStatic> ResolveAll(OffsetSchema schema, IReadOnlyDictionary<string, Func<ulong, bool>>? fingerprints = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var results = new List<ResolvedStatic>(schema.Statics.Count);
        foreach (StaticAnchor anchor in schema.Statics.Values)
        {
            Func<ulong, bool>? fingerprint = null;
            fingerprints?.TryGetValue(anchor.Name, out fingerprint);
            results.Add(Resolve(anchor, fingerprint));
        }

        return results;
    }

    /// <summary>Resolves one anchor: scan, disambiguate, follow the RIP displacement.</summary>
    /// <param name="fingerprint">
    /// Accepts a resolved address when what it leads to looks right. Decides between fallback
    /// hits; the primary pattern is trusted on a unique hit as before.
    /// </param>
    public ResolvedStatic Resolve(StaticAnchor anchor, Func<ulong, bool>? fingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(anchor);

        (BytePattern pattern, int caretOffset) = ParseWithCaret(anchor.Pattern);

        List<ulong> matches = _scanner.FindAll(pattern, maxMatches: 3);
        string primaryDetail;
        if (matches.Count == 1)
        {
            // RIP-relative: disp32 sits at the caret; the referencing instruction ends
            // right after it (caret + 4) for every pattern we use.
            ulong resolved = _scanner.ResolveRip(matches[0], caretOffset, caretOffset + 4);
            if (resolved != 0)
            {
                // A unique primary hit stands on its own, as it always has. The fingerprint
                // is still consulted so that the AreaChangeCounter lesson - a unique hit
                // that resolved a different counter - shows up as a remark on the row
                // rather than being discovered a session later.
                string remark = fingerprint is not null && !fingerprint(resolved)
                    ? " - but the fingerprint check REJECTS what it leads to; the pattern may have found a different site"
                    : string.Empty;
                return new ResolvedStatic(anchor.Name, resolved, $"match at 0x{matches[0]:X}{remark}");
            }

            primaryDetail = $"match at 0x{matches[0]:X} but RIP target implausible";
        }
        else
        {
            primaryDetail = matches.Count == 0 ? "pattern not found" : $"ambiguous: {matches.Count}+ matches";
        }

        if (anchor.Fallbacks.Count == 0)
        {
            return new ResolvedStatic(anchor.Name, 0, primaryDetail);
        }

        return ResolveByFallback(anchor, primaryDetail, fingerprint);
    }

    /// <summary>
    /// Tries the fallbacks in order and stops at the first that yields exactly one accepted
    /// address. Every examined hit is returned as a candidate, whether it was accepted or
    /// not, because the rejected ones are how a person sees WHY the accepted one is right.
    /// </summary>
    private ResolvedStatic ResolveByFallback(StaticAnchor anchor, string primaryDetail, Func<ulong, bool>? fingerprint)
    {
        var candidates = new List<StaticCandidate>();
        var tooLoose = new List<int>();

        for (int index = 0; index < anchor.Fallbacks.Count; index++)
        {
            (BytePattern pattern, int caretOffset) = ParseWithCaret(anchor.Fallbacks[index]);
            List<ulong> hits = _scanner.FindAll(pattern, maxMatches: MaxFallbackHits + 1);
            if (hits.Count > MaxFallbackHits)
            {
                // Too loose to mean anything here: noted in the detail rather than listed, so
                // a fallback that silently produced nothing is not mistaken for one that missed.
                tooLoose.Add(index + 1);
                continue;
            }

            var accepted = new HashSet<ulong>();
            int firstOfThisFallback = candidates.Count;
            foreach (ulong hit in hits)
            {
                ulong resolved = _scanner.ResolveRip(hit, caretOffset, caretOffset + 4);
                if (resolved == 0)
                {
                    continue;
                }

                bool ok;
                try
                {
                    // No fingerprint means nothing can tell the hits apart, so all of them
                    // count and the fallback resolves only when there is one.
                    ok = fingerprint is null || fingerprint(resolved);
                }
                catch (Exception)
                {
                    // A fingerprint that throws on garbage is a rejection, not a crash: it
                    // runs over whatever a loose pattern happened to find.
                    ok = false;
                }

                candidates.Add(new StaticCandidate(index + 1, hit, resolved, _scanner.BytesAt(hit, pattern.Length), ok));
                if (ok)
                {
                    accepted.Add(resolved);
                }
            }

            // Two sites referencing the SAME static are one answer, not an ambiguity: a
            // global is read from more than one function, and a loose pattern can find both.
            if (accepted.Count == 1)
            {
                ulong address = accepted.First();
                int sites = candidates.Count - firstOfThisFallback;
                string how = fingerprint is null
                    ? $"fallback {index + 1} hit once"
                    : $"fallback {index + 1}: {sites} hit(s), one fingerprinted address";
                return new ResolvedStatic(anchor.Name, address, $"{primaryDetail}; {how}", candidates);
            }
        }

        string outcome = candidates.Count == 0
            ? "no fallback hit either"
            : candidates.Any(c => c.Fingerprinted)
                ? "fallbacks hit but more than one address passed the fingerprint"
                : "fallbacks hit but nothing passed the fingerprint";
        if (tooLoose.Count > 0)
        {
            outcome += $"; fallback {string.Join(", ", tooLoose)} too loose ({MaxFallbackHits}+ hits, not listed)";
        }

        return new ResolvedStatic(anchor.Name, 0, $"{primaryDetail}; {outcome}", candidates);
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
