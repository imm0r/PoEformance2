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
/// The managed decoder stays as the fallback. OozSharp is a port of the ooz decoder - Kraken,
/// Mermaid, Selkie, Leviathan - and it is what runs when the install cannot be found or its
/// library will not load.
///
/// WHY THE ORDER IS THIS WAY ROUND, and it is not a preference. Measured on a real install
/// (2026-08-10): the managed decoder refuses the index outright - 565 chunks, 113,943,153 bytes
/// in, 147,897,312 expected out, and nothing comes back. The header and the chunk table read
/// perfectly in the same breath, so it is the decoder and not the framing. An install whose
/// pictures cannot be read is the whole item-art feature gone.
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
        if (Native.Load(gameFolder) is { } native)
        {
            return new Unpacker(native.Decompress, $"the install's own {native.Called}");
        }

        return new Unpacker(
            Decompress,
            string.IsNullOrEmpty(Native.LastProblem)
                ? "the shipped managed decoder"
                : $"the shipped managed decoder ({Native.LastProblem})");
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
        LastRefusal = $"{what} (chunk starts {Convert.ToHexString(head)})";
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

        private unsafe Native(string called, nint entry)
        {
            Called = called;
            _decompress = (delegate* unmanaged[Cdecl]<
                byte*, nint, byte*, nint, int, int, int, byte*, nint, void*, void*, void*, nint, int, nint>)entry;
        }

        /// <summary>What the file is called, for the line that says which decoder ran.</summary>
        public string Called { get; }

        /// <summary>Why there is no native decoder, when there is none.</summary>
        public static string LastProblem => _problem;

        /// <summary>Finds and loads it, or returns null and says why.</summary>
        public static Native? Load(string? gameFolder)
        {
            _problem = string.Empty;

            if (string.IsNullOrWhiteSpace(gameFolder) || !Directory.Exists(gameFolder))
            {
                _problem = "no game folder to take one from";
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
                        Directory.EnumerateFiles(gameFolder, pattern).Order(StringComparer.Ordinal).Reverse()),
                ];

                if (candidates.Length == 0)
                {
                    _problem = $"none of {string.Join(", ", LibraryPatterns)} in the game folder";
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
                        return new Native(called, entry);
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
