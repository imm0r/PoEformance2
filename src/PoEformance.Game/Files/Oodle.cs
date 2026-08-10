using System.Runtime.InteropServices;

namespace PoEformance.Game.Files;

/// <summary>
/// Undoing the compression the game's bundles use.
/// </summary>
/// <remarks>
/// TWO DECODERS, AND THE INSTALL'S OWN WINS WHEN IT HAS ONE. Oodle is a commercial compressor
/// and its library is not something this can ship - but the game cannot run without it, so a
/// copy is already on the machine. Loading THAT decodes exactly what THAT install packed, by
/// construction, and still ships nothing licensed. It is also what LibBundle3 does, and
/// LibBundle3 is where the bundle format in this project came from.
///
/// WHERE that copy is depends on the game. Path of Exile 1 ships <c>oo2core_*.dll</c> beside the
/// executable. Path of Exile 2 does NOT - checked on a real install, 2026-08-10, whose folder
/// holds bink2w64, the D3D compilers, fmod, Aftermath, XeSS and steam_api and no Oodle at all -
/// so there it is linked into something else. Hence a LIST of candidates, each ASKED for the
/// entry point rather than believed to have it.
///
/// The managed decoder is the fallback, AND IT CANNOT READ PATH OF EXILE 2. Its package says
/// "Kraken / Mermaid / Selkie / Leviathan"; its source says otherwise, in a comment - "Only need
/// Mermaid for Fortnite" - and Mermaid is the only quantum decoder in it. The word Leviathan
/// does not appear in that library at all.
///
/// PATH OF EXILE 2 PACKS WITH LEVIATHAN. Measured on a real install (2026-08-10): 565 chunks,
/// 113,943,153 bytes in, 147,897,312 expected out, and the very first chunk begins 8C 0C - the
/// low nibble of 0xC that says Oodle, and decoder type 12, which is Leviathan in ooz's own
/// Kraken_DecodeStep. The header and the chunk table read perfectly in the same breath, so it is
/// the decoder and nothing else.
///
/// WHAT THE MANAGED DECODER CALLED IT WAS WRONG: "Decoder type Selkie not supported". Its enum
/// starts at 1 where Oodle's starts at 0, so every name it reports is one out. Selkie is a real
/// codec and a wrong answer, and chasing it would have been a day. The name in a report is
/// therefore read off the chunk here rather than taken from the decoder - see Codec.
///
/// EVERYTHING AROUND THIS IS TESTED AND THIS IS NOT. There is no Oodle compressor to build a
/// fixture with, so the chunk table, the range arithmetic and the index are all checked against
/// data the tests build themselves, and both decoders are handed in. Which one ran, and why the
/// other did not, is therefore SAID rather than assumed - see <see cref="Unpacker.Which"/>.
/// </remarks>
public static class Oodle
{
    /// <summary>
    /// What the game's own decoder might be called, best first.
    /// </summary>
    /// <remarks>
    /// <c>oo2core</c> is Oodle shipped as its own library, which is how Path of Exile 1 does it.
    /// Path of Exile 2 does not: its folder holds no such file, so Oodle is linked into
    /// something else there.
    ///
    /// <c>bink2w64</c> is in the list because Bink is RAD Game Tools' video codec and Oodle is
    /// RAD's compressor - the same vendor, shipped together, and in some titles the one library
    /// carries the other's exports. Whether it does HERE is not assumed: the library is asked
    /// for OodleLZ_Decompress by name, and a library that does not have it is let go of again.
    ///
    /// Nothing else is tried, because probing means LOADING, and loading somebody's library
    /// runs their startup code. These two are both RAD decompressors that the game itself has
    /// already loaded; the rest of a game folder is not this tool's business.
    /// </remarks>
    public static readonly string[] LibraryPatterns = ["oo2core*.dll", "bink2w64.dll"];

    /// <summary>
    /// What a chunk's decoder byte means, in the reference's numbering.
    /// </summary>
    /// <remarks>
    /// OURS RATHER THAN THE DECODER'S, and that is not pedantry. OozSharp's enum starts at 1
    /// where Oodle's starts at 0, so every name it reports is one out: it called this install's
    /// Leviathan "Selkie", which is a real codec and a wrong answer, and the next person to read
    /// that line would go looking for the wrong decoder. Numbering from ooz's own
    /// Kraken_DecodeStep, which is where the format in this project came from.
    /// </remarks>
    private static readonly Dictionary<int, string> Codecs = new()
    {
        [5] = "LZNA", [6] = "Kraken", [10] = "Mermaid or Selkie", [11] = "BitKnit", [12] = "Leviathan",
    };

    private static readonly OozSharp.Kraken Decoder = new();
    private static readonly Lock Gate = new();

    /// <summary>Why the last chunk would not decompress, for a report to carry.</summary>
    /// <remarks>
    /// The decoder's own words. Thrown away before, which left "it will not decompress" as the
    /// whole of what anybody could find out - including whoever wrote it.
    /// </remarks>
    public static string LastRefusal { get; private set; } = string.Empty;

    /// <summary>How to undo Oodle, and which decoder that turned out to be.</summary>
    /// <param name="Decompress">One chunk in, its bytes out, null when it will not.</param>
    /// <param name="Which">Which decoder this is, and - when it is the fallback - why.</param>
    public readonly record struct Unpacker(Func<ReadOnlyMemory<byte>, int, byte[]?> Decompress, string Which);

    /// <summary>
    /// The best decoder available for an install: its own library, or the shipped fallback.
    /// </summary>
    /// <param name="gameFolder">Where the game is. Null or missing falls straight back.</param>
    public static Unpacker For(string? gameFolder)
    {
        // BESIDE THE TOOL AS WELL AS BESIDE THE GAME, and in its data folder. Path of Exile 2
        // ships no Oodle library at all, so the game folder is often a dead end - but anybody
        // who has Path of Exile 1, or another game that ships one, already has the file, and
        // putting it in either of the other two is then the whole of the setup.
        //
        // EACH PLACE IS NAMED IN THE REPORT. Written without the names first, every line said
        // "the game folder" about all three, including the two that are not it - which is a
        // message that does not know what it is talking about, and worse than none.
        (string Where, string? Folder)[] places =
        [
            ("the game folder", gameFolder),
            ("beside the tool", AppContext.BaseDirectory),
            ("the tool's data folder", Path.Combine(AppContext.BaseDirectory, "data")),
        ];

        var looked = new List<string>();
        foreach ((string where, string? folder) in places)
        {
            if (Native.Load(folder) is { } native)
            {
                return new Unpacker(native.Decompress, $"{native.Called} {where}");
            }

            looked.Add($"{where}: {Native.LastProblem}");
        }

        return new Unpacker(Decompress, $"the shipped managed decoder ({string.Join("; ", looked)})");
    }

    /// <summary>
    /// Decompresses one chunk with the managed decoder, or returns null when it will not.
    /// </summary>
    /// <param name="packed">The compressed chunk.</param>
    /// <param name="size">How big it is meant to come out - the decoder is told, not asked.</param>
    /// <remarks>
    /// A FAILURE HERE IS AN ICON, not a crash. A bundle that will not unpack means the install is
    /// a version this does not understand, or the file is damaged, and neither is worth taking
    /// the overlay down for - the caller falls back to drawing the item's name.
    ///
    /// NotImplementedException is caught with the rest, and it was not before: the decoder is a
    /// port and has paths it does not implement, so a bundle that reached one took the whole
    /// process down from a draw. That is the one exception here that was a crash rather than a
    /// missing picture.
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
                if (plain.Length >= size)
                {
                    return plain.Span[..size].ToArray();
                }

                Refused($"gave back {plain.Length} bytes of the {size} asked for", packed);
                return null;
            }
        }
        catch (Exception exception) when (exception is OozSharp.Exceptions.DecoderException
                                              or NotImplementedException or ArgumentException
                                              or IndexOutOfRangeException or ArgumentOutOfRangeException
                                              or OverflowException)
        {
            Refused($"{exception.GetType().Name}: {exception.Message}", packed);
            return null;
        }
    }

    /// <summary>Writes down what a decoder said, with the head of the chunk it said it about.</summary>
    /// <remarks>
    /// The first bytes because they are where a chunk names its own compressor: told them,
    /// somebody who knows the format can say which one this install packs with, from a log line.
    /// </remarks>
    private static void Refused(string what, ReadOnlyMemory<byte> packed)
    {
        ReadOnlySpan<byte> head = packed.Span[..Math.Min(8, packed.Length)];
        LastRefusal = $"{what} - the chunk is {Codec(packed.Span)} ({Convert.ToHexString(head)})";
    }

    /// <summary>
    /// Which compressor a chunk names itself as.
    /// </summary>
    /// <remarks>
    /// A chunk says what it is in its first two bytes: the low nibble of the first is 0xC on
    /// every Oodle chunk, and the low seven bits of the second are the decoder. Read here rather
    /// than taken from whatever the decoder that just failed calls it - see <see cref="Codecs"/>.
    /// </remarks>
    public static string Codec(ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length < 2 || (chunk[0] & 0xF) != 0xC)
        {
            return "not an Oodle chunk";
        }

        int type = chunk[1] & 0x7F;
        return Codecs.TryGetValue(type, out string? named) ? $"{named} ({type})" : $"decoder type {type}";
    }

    /// <summary>
    /// The game's own Oodle library, loaded out of its own folder.
    /// </summary>
    /// <remarks>
    /// Loaded BY PATH rather than by name, and called through a function pointer rather than a
    /// DllImport, for the same reason: the library is not beside this executable and never will
    /// be - it belongs to the game. Nothing is copied, nothing is shipped, and nothing is left
    /// behind; the handle lives as long as the tool does.
    /// </remarks>
    private sealed class Native
    {
        private static string _problem = string.Empty;

        private readonly unsafe delegate* unmanaged[Cdecl]<
            byte*, nint, byte*, nint, int, int, int, byte*, nint, void*, void*, void*, nint, int, nint> _decompress;

        private unsafe Native(string where, nint entry)
        {
            Where = where;
            Called = Path.GetFileName(where);
            _decompress = (delegate* unmanaged[Cdecl]<
                byte*, nint, byte*, nint, int, int, int, byte*, nint, void*, void*, void*, nint, int, nint>)entry;
        }

        /// <summary>What the file is called, for the line that says which decoder ran.</summary>
        public string Called { get; }

        /// <summary>And where it was, because which COPY answered is half of that line.</summary>
        public string Where { get; }

        /// <summary>Why there is no native decoder, when there is none.</summary>
        public static string LastProblem => _problem;

        /// <summary>Finds and loads it from one folder, or returns null and says why.</summary>
        public static Native? Load(string? folder)
        {
            _problem = string.Empty;

            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                _problem = "not there";
                return null;
            }

            try
            {
                // Matched by pattern, because the number in an oo2core name is the Oodle major
                // version and changes under the tool's feet. Newest last within a pattern, so
                // the highest version wins; patterns in order, so oo2core beats its stand-ins.
                string[] candidates =
                [
                    .. LibraryPatterns.SelectMany(pattern =>
                        Directory.EnumerateFiles(folder, pattern).Order(StringComparer.Ordinal).Reverse()),
                ];

                if (candidates.Length == 0)
                {
                    _problem = $"no {string.Join(" or ", LibraryPatterns)}";
                    return null;
                }

                var refused = new List<string>();
                foreach (string file in candidates)
                {
                    string called = Path.GetFileName(file);

                    if (!NativeLibrary.TryLoad(file, out nint library))
                    {
                        refused.Add($"{called} would not load");
                        continue;
                    }

                    // ASKED, NOT ASSUMED. A library either exports the entry point or it does
                    // not, and this is the question itself rather than a belief about which
                    // file carries Oodle - which is not knowable from a name.
                    if (!NativeLibrary.TryGetExport(library, "OodleLZ_Decompress", out nint entry))
                    {
                        NativeLibrary.Free(library);
                        refused.Add($"{called} has no OodleLZ_Decompress");
                        continue;
                    }

                    unsafe
                    {
                        return new Native(file, entry);
                    }
                }

                _problem = string.Join("; ", refused);
                return null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or DllNotFoundException or BadImageFormatException
                                                  or ArgumentException)
            {
                _problem = $"{exception.GetType().Name}: {exception.Message}";
                return null;
            }
        }

        /// <summary>
        /// One chunk, through the game's own decoder.
        /// </summary>
        /// <remarks>
        /// Every argument is passed rather than defaulted, because a function pointer has no
        /// defaults to inherit: fuzz-safe on, CRC off, silent, no dictionary, no callback, and
        /// the decoder allocating its own scratch. The last is the unthreaded phase, which is
        /// the only one that decodes a whole chunk in one call.
        ///
        /// A zero back means it refused. It is not an exception and must not become one - this
        /// runs while a stash is being drawn.
        /// </remarks>
        public unsafe byte[]? Decompress(ReadOnlyMemory<byte> packed, int size)
        {
            if (packed.IsEmpty || size <= 0)
            {
                return null;
            }

            var plain = new byte[size];
            nint got;

            fixed (byte* input = packed.Span)
            fixed (byte* output = plain)
            {
                lock (Gate)
                {
                    got = _decompress(
                        input, packed.Length, output, size,
                        1, 0, 0,
                        null, 0, null, null, null, 0, 3);
                }
            }

            if (got >= size)
            {
                return plain;
            }

            LastRefusal = $"{Called} decoded {got} bytes of the {size} asked for";
            return null;
        }
    }
}
