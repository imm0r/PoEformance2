using System.Numerics;

namespace PoEformance.Features;

/// <summary>
/// Which rows of a table are in play, one bit each.
/// </summary>
/// <remarks>
/// WHY BITS AND NOT A LIST OF ROW NUMBERS, which is the whole reason this type exists. A facet rail
/// shows, beside every value it offers, how many rows would be left if that value were added to the
/// filter - and over the monster table there are some nine thousand such values. Counted by walking
/// the rows for each one that is nine thousand times two and a half thousand, twenty-three million
/// operations, on every keystroke. Counted as an AND of two bitsets it is forty-three machine words
/// and forty-three popcounts per value: the whole rail is under half a million popcounts, which is
/// a fraction of a millisecond and only runs when the filter changes.
///
/// THE OPERATIONS ARE IN PLACE, which matters more than it looks. A filter is built by narrowing
/// one set over and over, and a version that returned a fresh set per step would allocate one per
/// term per keystroke. Nothing here allocates once the set is made.
///
/// 2733 ROWS IS 43 WORDS, 344 BYTES. Nine thousand values indexed this way is about three megabytes,
/// measured rather than feared - against a 1.5 MB table already in memory and a tool that reads
/// another process for a living, that is not a cost worth a cleverer structure. It would become one
/// at ten times the rows: see docs/reading-big-tables.md, "Where this design stops working".
/// </remarks>
public sealed class RowSet
{
    private const int Bits = 64;

    private readonly ulong[] _words;

    /// <summary>A set over this many rows, holding none of them.</summary>
    public RowSet(int rows)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rows);
        Rows = rows;
        _words = new ulong[(rows + Bits - 1) / Bits];
    }

    /// <summary>How many rows the set is over - not how many are in it.</summary>
    public int Rows { get; }

    /// <summary>How many rows are in it.</summary>
    public int Count
    {
        get
        {
            var count = 0;
            foreach (ulong word in _words)
            {
                count += BitOperations.PopCount(word);
            }

            return count;
        }
    }

    /// <summary>Empties it.</summary>
    public void None() => Array.Clear(_words);

    /// <summary>Fills it with every row.</summary>
    public void All()
    {
        Array.Fill(_words, ulong.MaxValue);
        Tidy();
    }

    /// <summary>Whether a row is in it.</summary>
    public bool Has(int row)
        => (uint)row < (uint)Rows && (_words[row / Bits] & (1UL << (row % Bits))) != 0UL;

    /// <summary>Puts a row in it. A row it is not over is ignored.</summary>
    public void Add(int row)
    {
        if ((uint)row < (uint)Rows)
        {
            _words[row / Bits] |= 1UL << (row % Bits);
        }
    }

    /// <summary>Keeps only the rows that are in both.</summary>
    public void And(RowSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        int shared = Math.Min(_words.Length, other._words.Length);

        for (var at = 0; at < shared; at++)
        {
            _words[at] &= other._words[at];
        }

        for (int at = shared; at < _words.Length; at++)
        {
            _words[at] = 0UL;
        }
    }

    /// <summary>Takes in the rows that are in either.</summary>
    public void Or(RowSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        int shared = Math.Min(_words.Length, other._words.Length);

        for (var at = 0; at < shared; at++)
        {
            _words[at] |= other._words[at];
        }

        Tidy();
    }

    /// <summary>Turns it inside out: every row it held, it now does not.</summary>
    public void Not()
    {
        for (var at = 0; at < _words.Length; at++)
        {
            _words[at] = ~_words[at];
        }

        Tidy();
    }

    /// <summary>Makes this set a copy of that one.</summary>
    public void CopyFrom(RowSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        int shared = Math.Min(_words.Length, other._words.Length);
        Array.Copy(other._words, _words, shared);

        for (int at = shared; at < _words.Length; at++)
        {
            _words[at] = 0UL;
        }

        Tidy();
    }

    /// <summary>
    /// How many rows are in both, without changing either.
    /// </summary>
    /// <remarks>
    /// THE FACET RAIL'S WHOLE INNER LOOP. Counting the overlap by making a copy, ANDing it and
    /// counting that would allocate a set per value offered - nine thousand of them per keystroke.
    /// </remarks>
    public int CountAnd(RowSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        int shared = Math.Min(_words.Length, other._words.Length);
        var count = 0;

        for (var at = 0; at < shared; at++)
        {
            count += BitOperations.PopCount(_words[at] & other._words[at]);
        }

        return count;
    }

    /// <summary>Writes the rows it holds into a list, smallest first.</summary>
    public void CopyTo(List<int> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();

        for (var at = 0; at < _words.Length; at++)
        {
            ulong word = _words[at];
            while (word != 0UL)
            {
                int bit = BitOperations.TrailingZeroCount(word);
                into.Add((at * Bits) + bit);
                word &= word - 1UL;
            }
        }
    }

    /// <summary>
    /// Clears the bits past the last row, which no operation may leave standing.
    /// </summary>
    /// <remarks>
    /// THE ONE PLACE A BITSET GOES WRONG QUIETLY. The last word holds up to 63 bits that are not
    /// rows of anything, and a count that includes them is too high by a number with no explanation
    /// - so every operation that can set them ends here rather than leaving it to its caller.
    /// </remarks>
    private void Tidy()
    {
        int spare = (_words.Length * Bits) - Rows;
        if (spare > 0 && _words.Length > 0)
        {
            _words[^1] &= ulong.MaxValue >> spare;
        }
    }
}
