using System.Globalization;

namespace PoEformance.Core.Scanning;

/// <summary>
/// A byte signature with wildcards, e.g. <c>"48 8B 05 ?? ?? ?? ?? 48 85 C0"</c>.
/// </summary>
/// <remarks>
/// Parsed once, matched many times. <c>??</c> (or <c>?</c>) matches any byte. Patterns
/// live in the offset schema next to the structs they anchor, so this type only knows
/// how to parse and match - not where patterns come from.
/// </remarks>
public sealed class BytePattern
{
    private readonly byte[] _bytes;
    private readonly bool[] _isWildcard;

    private BytePattern(string text, byte[] bytes, bool[] isWildcard)
    {
        Text = text;
        _bytes = bytes;
        _isWildcard = isWildcard;
    }

    /// <summary>The original pattern text, for diagnostics.</summary>
    public string Text { get; }

    /// <summary>Number of bytes the pattern covers.</summary>
    public int Length => _bytes.Length;

    /// <summary>
    /// Parses a space-separated hex pattern. Throws <see cref="FormatException"/> with
    /// the offending token on bad input - patterns are data, and data errors must name
    /// the culprit.
    /// </summary>
    public static BytePattern Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        string[] tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var bytes = new byte[tokens.Length];
        var wildcards = new bool[tokens.Length];

        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];
            if (token is "??" or "?")
            {
                wildcards[i] = true;
            }
            else if (!byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
            {
                throw new FormatException($"Bad pattern token '{token}' at position {i} in \"{text}\".");
            }
        }

        if (bytes.Length == 0)
        {
            throw new FormatException("Empty pattern.");
        }

        if (wildcards[0])
        {
            throw new FormatException($"Pattern must not start with a wildcard: \"{text}\".");
        }

        return new BytePattern(text, bytes, wildcards);
    }

    /// <summary>
    /// Finds all matches in <paramref name="haystack"/>, up to <paramref name="maxMatches"/>.
    /// Returns offsets into the haystack.
    /// </summary>
    /// <remarks>
    /// Strategy: IndexOf on the first (never-wildcard) byte to skip ahead using the
    /// vectorised runtime search, then verify the tail manually. Simple and fast enough
    /// to sweep a 50 MB module in well under a second.
    /// </remarks>
    public List<int> FindAll(ReadOnlySpan<byte> haystack, int maxMatches = int.MaxValue)
    {
        var matches = new List<int>();
        byte first = _bytes[0];
        int limit = haystack.Length - _bytes.Length;
        int position = 0;

        while (position <= limit)
        {
            int found = haystack[position..].IndexOf(first);
            if (found < 0)
            {
                break;
            }

            position += found;
            if (position > limit)
            {
                break;
            }

            if (MatchesAt(haystack, position))
            {
                matches.Add(position);
                if (matches.Count >= maxMatches)
                {
                    break;
                }
            }

            position++;
        }

        return matches;
    }

    private bool MatchesAt(ReadOnlySpan<byte> haystack, int offset)
    {
        for (int i = 1; i < _bytes.Length; i++)
        {
            if (!_isWildcard[i] && haystack[offset + i] != _bytes[i])
            {
                return false;
            }
        }

        return true;
    }
}
