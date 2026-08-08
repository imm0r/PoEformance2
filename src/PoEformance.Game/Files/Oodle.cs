namespace PoEformance.Game.Files;

/// <summary>
/// Undoing the compression the game's bundles use.
/// </summary>
/// <remarks>
/// Oodle is a commercial compressor, and its own library is not something this can ship. What it
/// can do is DECOMPRESS, which is a published-enough format that there is a managed
/// reimplementation of it - OozSharp, a port of the ooz decoder. So there is no native DLL to
/// carry, no copy of somebody's licensed library in the repository, and nothing extra for
/// somebody to install: it is a NuGet package that reads bytes and writes bytes.
///
/// WHICH COMPRESSOR is in the compressed data, not in the bundle header - each chunk names its
/// own. Kraken, Mermaid, Selkie and Leviathan all turn up in the game's bundles, and the decoder
/// handles all of them, so nothing here has to care which a particular bundle used.
///
/// THE ONE PART THAT IS NOT TESTED WITHOUT THE GAME. Everything around it - the chunk table, the
/// range arithmetic, the index - is checked against data this builds itself. This cannot be:
/// there is no compressor to make a fixture with, so proving it works needs real bundles from a
/// real install.
/// </remarks>
public static class Oodle
{
    private static readonly OozSharp.Kraken Decoder = new();

    /// <summary>
    /// Decompresses one chunk, or returns null when it will not decompress.
    /// </summary>
    /// <param name="packed">The compressed chunk.</param>
    /// <param name="size">How big it is meant to come out - the decoder is told, not asked.</param>
    /// <remarks>
    /// A FAILURE HERE IS AN ICON, not a crash. A bundle that will not unpack means the install is
    /// a version this does not understand, or the file is damaged, and neither is worth taking
    /// the overlay down for - the caller falls back to drawing the item's name.
    /// </remarks>
    public static byte[]? Decompress(ReadOnlyMemory<byte> packed, int size)
    {
        if (packed.IsEmpty || size <= 0)
        {
            return null;
        }

        try
        {
            // The decoder is not documented as safe to share, and a stash opening can ask for
            // several chunks at once, so one at a time through here.
            lock (Decoder)
            {
                ReadOnlyMemory<byte> plain = Decoder.Decompress(packed.Span, size);
                return plain.Length >= size ? plain.Span[..size].ToArray() : null;
            }
        }
        catch (Exception exception) when (exception is OozSharp.Exceptions.DecoderException
                                              or ArgumentException or IndexOutOfRangeException
                                              or ArgumentOutOfRangeException or OverflowException)
        {
            return null;
        }
    }
}
